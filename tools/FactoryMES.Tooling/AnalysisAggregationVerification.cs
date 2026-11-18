using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class AnalysisAggregationVerification
{
    public static async Task RunAsync(MesDbContext db, Func<string, string> secret)
    {
        var checks = new List<string>();
        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException(label);
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        using var pinned = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client(string address) => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (
                _,
                cert,
                _,
                _
            ) => cert?.Thumbprint == pinned.Thumbprint
        }) {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var anonymous = Client("https://web:8443");
        using var jobs = Client("http://mes-backend:8080");
        using var wrong = Client("http://mes-backend:8080");
        async Task<HttpResponseMessage> Post(
            HttpClient client,
            string route,
            object body
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(body, body.GetType())
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
            return await client.SendAsync(request);
        }
        async Task<T> Read<T>(
            HttpResponseMessage response,
            int status,
            string label
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == status, label + " HTTP " + (int)response
                    .StatusCode);
                return (await response.Content.ReadFromJsonAsync<T>())!;
            }
        }
        async Task Expect(
            HttpResponseMessage response,
            int status,
            string label
        )
        {
            using (response) Check((int)response.StatusCode == status,
                label + " HTTP " + (int)response.StatusCode);
        }
        async Task<T> Wait<T>(Func<Task<T?>> read, string label) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(65);
            while (DateTime.UtcNow < until)
            {
                var result = await read();
                if (result is not null) { Check(true, label); return result; }
                await Task.Delay(300);
            }
            throw new InvalidOperationException("Timed out: " + label);
        }
        async Task Authenticate(HttpClient client, string identity)
        {
            using var response = await jobs.PostAsJsonAsync("/internal/v1/auth/token", new
            {
                clientId = identity,
                clientSecret = secret(identity + "_client_secret"),
                audience = "mes-backend"
            });
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                token.GetProperty("accessToken").GetString());
        }
        var me = await Read<SessionUser>(
            await Post(
                admin,
                "/api/v1/auth/login",
                new {
                    login = "admin",
                    password = secret("admin_password")
                }
            ),
            200,
            "Admin login"
        );
        await Read<SessionUser>(
            await Post(
                worker,
                "/api/v1/auth/login",
                new {
                    login = "portal",
                    password = secret("portal_password")
                }
            ),
            200,
            "Worker login"
        );
        await Authenticate(jobs, "jobs-worker");
        await Authenticate(wrong, "verification");
        var key = "AG" + Guid.NewGuid().ToString("N")[..12];
        var ai = await Read<AiTargetView>(
            await Post(
                admin,
                "/api/v1/ai-targets",
                new MockAiTargetCreate(key + "AI", "Aggregation verification")
            ),
            201,
            "Create Mock analysis target"
        );
        var line = new Line {
            Code = key,
            Name = "Aggregation fixture"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new Equipment {
            LineId = line.LineId,
            Code = key,
            Name = "Aggregation fixture"
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        var source = new IntegrationTarget
        {
            TargetCode = key + "SRC",
            Kind = "EQUIPMENT",
            ConnectorType = "equipment-http",
            DisplayName = "Disabled direct SQL fixture",
            EquipmentId = equipment.EquipmentId,
            Enabled = false,
            ConfigVersion = 1
        };
        db.Add(source);
        await db.SaveChangesAsync();
        db.Add(new EquipmentConnection
        {
            TargetId = source.TargetId,
            Kind = "EQUIPMENT",
            Protocol = "HTTP",
            ApprovedEndpoint = "http://equipment-simulator:8080/equipment/" + key,
            PollIntervalMs = 1000,
            TimeoutMs = 1000,
            StaleSec = 60
        });
        await db.SaveChangesAsync();
        async Task<EquipmentTagMapping> Mapping(string code, string kind = "NUMBER")
        {
            var mapping = new EquipmentTagMapping
            {
                TargetId = source.TargetId,
                MetricCode = code,
                SourcePath = kind == "STATE" ? "operatingState" : "temperature",
                DataType = kind == "STATE" ? "STRING" : "DECIMAL",
                ValueKind = kind,
                Unit = kind == "STATE" ? null : "C",
                Scale = 1
            };
            db.Add(mapping);
            await db.SaveChangesAsync();
            return mapping;
        }
        var temperature = await Mapping("TEMPERATURE");
        var counter = await Mapping("COUNT_VALUE");
        var stateMapping = await Mapping("STATE_VALUE", "STATE");
        var server = (await admin.GetFromJsonAsync<AnalysisOptions>("/api/v1/analysis-options"))!
            .ServerTime;
        var end = DateTime.SpecifyKind(server.AddHours(9).Date.AddHours(-7), DateTimeKind.Utc);
        if (end > server) end = end.AddDays(-1);
        var start = end.AddDays(-1);
        var stream = Guid.NewGuid().ToString();
        var prefix = Guid.NewGuid().ToString()[..24];
        var payload = "{}";
        var payloadHash = CollectionFormat.Hash(new { });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO TELEMETRY_EVENT
                (EVENT_ID,EQUIPMENT_ID,TARGET_ID,STREAM_ID,SEQUENCE_NO,SCHEMA_VERSION,
                 CONFIG_VERSION,OCCURRED_AT_UTC,RECEIVED_AT_UTC,PAYLOAD,PAYLOAD_HASH)
            SELECT {prefix} || LPAD(LEVEL,12,'0'), {equipment.EquipmentId}, {source.TargetId},
                {stream}, LEVEL, 1, 1,
                {start} + NUMTODSINTERVAL(LEVEL-1,'SECOND'),
                {start} + NUMTODSINTERVAL(LEVEL-1,'SECOND'), {payload}, {payloadHash}
            FROM DUAL CONNECT BY LEVEL <= 86400
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO TELEMETRY_METRIC
                (TELEMETRY_ID,METRIC_CODE,MAPPING_ID,NUMERIC_VALUE,UNIT,QUALITY)
            SELECT TELEMETRY_ID,'TEMPERATURE',{temperature.MappingId},
                MOD(SEQUENCE_NO-1,100),'C','GOOD'
            FROM TELEMETRY_EVENT WHERE STREAM_ID={stream}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO TELEMETRY_METRIC
                (TELEMETRY_ID,METRIC_CODE,MAPPING_ID,NUMERIC_VALUE,UNIT,QUALITY)
            SELECT TELEMETRY_ID,'COUNT_VALUE',{counter.MappingId},SEQUENCE_NO,'C','GOOD'
            FROM TELEMETRY_EVENT WHERE STREAM_ID={stream}
            """);
        var seedMs = timer.ElapsedMilliseconds;
        Check(await db.Set<TelemetryMetric>().CountAsync(x => x.MappingId == temperature.MappingId
            || x.MappingId == counter.MappingId) == 172800,
                "86400 samples and 172800 numeric rows seeded without resetting data");

        async Task<TelemetryEvent> Sample(
            DateTime time,
            decimal? value,
            string quality = "GOOD",
            string? unit = "C"
        )
        {
            var sample = new TelemetryEvent
            {
                EventId = Guid.NewGuid().ToString(),
                EquipmentId = equipment.EquipmentId,
                TargetId = source.TargetId,
                StreamId = Guid.NewGuid().ToString(),
                SequenceNo = 1,
                SchemaVersion = 1,
                ConfigVersion = 1,
                OccurredAtUtc = time,
                ReceivedAtUtc = time,
                Payload = payload,
                PayloadHash = payloadHash
            };
            db.Add(sample);
            await db.SaveChangesAsync();
            db.Add(new TelemetryMetric
            {
                TelemetryId = sample.TelemetryId,
                MappingId = value is null ? stateMapping.MappingId : temperature.MappingId,
                MetricCode = value is null ? stateMapping.MetricCode : temperature.MetricCode,
                NumericValue = value,
                TextValue = value is null ? "RUN" : null,
                Unit = unit,
                Quality = quality
            });
            await db.SaveChangesAsync();
            return sample;
        }
        await Sample(start.AddTicks(-10), 9999);
        await Sample(end, 9999);
        await Sample(
            start.AddMinutes(2),
            9999,
            "BAD"
        );
        await Sample(
            start.AddMinutes(3),
            9999,
            "UNCERTAIN"
        );
        await Sample(start.AddMinutes(4), null);

        async Task<(AnalysisJobView Job, AnalysisInput Input)> Analyze(DateTime from, DateTime to)
        {
            var job = await Read<AnalysisJobView>(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    new AnalysisRequest(
                        Guid.NewGuid().ToString(),
                        Id(equipment.EquipmentId),
                        ai.TargetId,
                        from,
                        to,
                        "TREND_SUMMARY"
                    )
                ),
                202,
                "Capture pre-aggregated analysis input"
            );
            var snapshotId = long.Parse(job.Snapshot!.SnapshotId);
            var snapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x => x
                .SnapshotId == snapshotId);
            var input = JsonSerializer.Deserialize<AnalysisInput>(snapshot.Payload,
                CollectionFormat.Json)!;
            Check(
                CollectionFormat.Hash(input) == snapshot.InputHash,
                "Aggregated snapshot hash matches canonical input"
            );
            return (job, input);
        }
        timer.Restart();
        var large = await Analyze(start, end);
        var captureMs = timer.ElapsedMilliseconds;
        Check(large.Input.SampleCount == 86400 && large.Input.Coverage == "1",
            "Distinct samples and union coverage survive multi-metric aggregation");
        Check(
            large.Input.Metrics.Length == 2,
            "Only grouped metrics, not raw rows, enter the snapshot"
        );
        var temp = large.Input.Metrics.Single(x => x.MetricCode == "TEMPERATURE");
        var count = large.Input.Metrics.Single(x => x.MetricCode == "COUNT_VALUE");
        Check(temp.Minimum == "0" && temp.Maximum == "99" && temp.Mean == "49.5" && temp
            .Last == "99",
            "24-hour temperature statistics are exact with excluded bounds and bad quality");
        Check(count.Minimum == "1" && count.Maximum == "86400" && count.Mean == "43200.5" && count
            .Last == "86400",
            "Second metric retains exact min/max/weighted mean/last");
        await Wait(async () =>
        {
            var job = await admin.GetFromJsonAsync<AnalysisJobView>("/api/v1/analyses/" + large
                .Job.JobId);
            if (job?.State is "FAILED" or "TIMED_OUT") throw new InvalidOperationException(job
                .ErrorCode);
            return job?.State == "SUCCEEDED" ? job : null;
        }, "Large 24-hour input completes through Gateway and Mock Connector");

        var small = start.AddDays(-2).AddTicks(5000000);
        await Sample(small, 0);
        await Sample(small.AddSeconds(60).AddTicks(-10), 0);
        await Sample(small.AddSeconds(60), 100);
        await Sample(small.AddSeconds(61), 100);
        await Sample(small.AddSeconds(62), 100);
        await Sample(small.AddSeconds(119), 50);
        await Sample(small.AddSeconds(119), 80);
        await Sample(small.AddMinutes(2), 9999);
        var unequal = await Analyze(small, small.AddMinutes(2));
        var unequalMetric = unequal.Input.Metrics.Single();
        Check(unequal.Input.SampleCount == 7 && unequalMetric.Mean == "61.428571",
            "Unequal minute buckets use value-weighted mean, never mean-of-means");
        Check(
            unequalMetric.Last == "80",
            "Timestamp ties use telemetry identity for deterministic last value"
        );
        var fraction = small.AddHours(1);
        await Sample(fraction.AddSeconds(60).AddTicks(-10), 1);
        await Sample(fraction.AddSeconds(60), 3);
        var fractional = await Analyze(fraction, fraction.AddMinutes(2));
        Check(fractional.Input.Coverage == "1" && fractional.Input.SampleCount == 2,
            "Request-relative minute buckets preserve fractional-second boundaries");
        var mixed = small.AddHours(2);
        await Sample(
            mixed,
            1,
            unit: null
        );
        await Sample(mixed.AddSeconds(1), 2);
        await Expect(
            await Post(
                admin,
                "/api/v1/analyses",
                new AnalysisRequest(
                    Guid.NewGuid().ToString(),
                    Id(equipment.EquipmentId),
                    ai.TargetId,
                    mixed,
                    mixed.AddMinutes(2),
                    "TREND_SUMMARY"
                )
            ),
            422,
            "NULL and named units cannot be silently combined"
        );

        var selectedCode = await (
            from target in db.Set<IntegrationTarget>()
            join config in db.Set<AiProviderConfig>() on target.TargetId equals config.TargetId
            where target.Kind == "AI" && target.Enabled == EF.Parameter(true)
                && target.Deleted == EF.Parameter(false) && config.ProviderKind == "MOCK"
            orderby target.TargetId select target.TargetCode
        ).FirstAsync();
        var identity = System.Text.Encoding.UTF8.GetBytes(selectedCode + ":" + equipment
            .EquipmentId + ":" + end.Ticks);
        var requestId = new Guid(System.Security.Cryptography.SHA256.HashData(identity).AsSpan(0,
            16)).ToString();
        await Read<ReportingDispatchResult>(
            await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
            200,
            "Scheduled analysis accepts a high-volume 24-hour window"
        );
        var scheduled = await Wait(async () =>
        {
            var row = await db.Set<IntegrationJob>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Principal == "SERVICE:jobs-worker" && x.RequestId == requestId && x
                    .Operation == "AI_ANALYZE");
            if (row?.State is "FAILED" or "TIMED_OUT") throw new InvalidOperationException(row
                .Error);
            return row?.State == "SUCCEEDED" ? row : null;
        }, "Scheduled Mock analysis succeeds instead of failing at the former raw-row cap");
        var scheduledResult = await admin
            .GetFromJsonAsync<AnalysisJobView>("/api/v1/analyses/" + scheduled.JobId);
        Check(scheduledResult!.Results.Length == 1 && scheduledResult.Results[0].MockFlag
            && !scheduledResult.Results[0].Accepted && scheduledResult.Results[0].Risk == "UNKNOWN",
            "Scheduled large-data result remains reference-only Mock");
        Check(scheduledResult.Snapshot!.InputHash == large.Job.Snapshot!.InputHash,
            "Manual and scheduled identical windows have identical immutable input hashes");
        await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { });
        Check(await db.Set<IntegrationJob>().CountAsync(x => x.Principal == "SERVICE:jobs-worker"
            && x.RequestId == requestId && x.Operation == "AI_ANALYZE") == 1,
            "Replayed schedule does not duplicate large-data analysis");
        Check(!await db.Set<Alarm>().AnyAsync(x => x.EquipmentId == equipment.EquipmentId),
            "Pre-aggregation and Mock analysis create no maintenance alarm");
        var targetRow = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetCode == ai
            .TargetId);
        targetRow.Enabled = false;
        await db.SaveChangesAsync();
        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/analysis-aggregation-verification.json",
            JsonSerializer.Serialize(new
        {
            passed = checks.Count,
            checks,
            rawSamples = 86400,
            rawNumericRows = 172800,
            groupedMetrics = large.Input.Metrics.Length,
            seedMilliseconds = seedMs,
            captureMilliseconds = captureMs,
            scheduledJobId = scheduled.JobId,
            cloudApiCalls = 0,
            externalDeliveryCalls = 0,
            note = "Direct isolated Oracle volume fixture, not actual PLC throughput or model inference."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Analysis aggregation verification passed: " + checks.Count);
    }

    private static string Id(long value) => value.ToString(System.Globalization.CultureInfo
        .InvariantCulture);
}

