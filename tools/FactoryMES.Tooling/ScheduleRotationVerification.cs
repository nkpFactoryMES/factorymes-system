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

public static class ScheduleRotationVerification
{
    public static async Task ReplayAsync(MesDbContext db, Func<string, string> secret)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            "/reports/analysis-rotation-verification.json"));
        var requests = document.RootElement.GetProperty("expectedRequests")
            .EnumerateArray().Select(x => x.GetString()!).ToArray();
        using var client = new HttpClient {
            BaseAddress = new Uri("http://mes-backend:8080")
        };
        using var tokenResponse = await client.PostAsJsonAsync("/internal/v1/auth/token", new
        {
            clientId = "jobs-worker",
            clientSecret = secret("jobs-worker_client_secret"),
            audience = "mes-backend"
        });
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.GetProperty("accessToken").GetString());
        using var response = await client.PostAsJsonAsync("/internal/v1/analyses/scheduled",
            new { });
        response.EnsureSuccessStatusCode();
        var batch = await response.Content.ReadFromJsonAsync<ReportingDispatchResult>();
        var rows = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => requests.Contains(x.RequestId) && x.Principal == "SERVICE:jobs-worker")
            .ToArrayAsync();
        var valid = rows.Length == 28 && rows.Count(x => x.State == "SUCCEEDED") == 27
            && rows.Count(x => x.State == "FAILED" && x.SnapshotId == null) == 1
            && rows.All(x => x.Attempt == 1) && batch!.Examined == 0;
        if (!valid) throw new InvalidOperationException("Durable rotation replay after restart failed");
        await File.WriteAllTextAsync("/reports/analysis-rotation-restart-verification.json",
            JsonSerializer.Serialize(new
            {
                passed = 1,
                checks = new[] {
                    "Backend restart preserves 28 terminal scheduled jobs without requeue"
                },
                cloudApiCalls = 0
            }, new JsonSerializerOptions {
                WriteIndented = true
            }));
        Console.WriteLine("PASS Durable rotation replay after backend restart");
    }

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
        var key = "RT" + Guid.NewGuid().ToString("N")[..10];
        await Read<AiTargetView>(
            await Post(
                admin,
                "/api/v1/ai-targets",
                new MockAiTargetCreate(key + "AI", "Rotation verification")
            ),
            201,
            "Ensure local Mock schedule target"
        );
        var selected = await (
            from target in db.Set<IntegrationTarget>()
            join config in db.Set<AiProviderConfig>() on target.TargetId equals config.TargetId
            where target.Kind == "AI" && target.Enabled == EF.Parameter(true)
                && target.Deleted == EF.Parameter(false) && config.ProviderKind == "MOCK"
            orderby target.TargetId select target
        ).AsNoTracking().FirstAsync();
        var server = (await admin.GetFromJsonAsync<AnalysisOptions>("/api/v1/analysis-options"))!
            .ServerTime;
        var end = DateTime.SpecifyKind(server.AddHours(9).Date.AddHours(-7), DateTimeKind.Utc);
        if (end > server) end = end.AddDays(-1);
        var start = end.AddDays(-1);
        var line = new Line {
            Code = key,
            Name = "Rotation isolated fixture"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        string RequestId(long equipment)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(selected
                .TargetCode + ":" + equipment + ":" + end.Ticks);
            return new Guid(System.Security.Cryptography.SHA256.HashData(bytes).AsSpan(0, 16))
                .ToString();
        }
        async Task<Equipment> EquipmentAsync(string suffix, bool active = true)
        {
            var row = new Equipment
            {
                Code = key + suffix,
                Name = "Rotation " + suffix,
                LineId = line.LineId,
                Active = active
            };
            db.Add(row);
            await db.SaveChangesAsync();
            return row;
        }
        async Task SeedAsync(Equipment equipment)
        {
            var source = new IntegrationTarget
            {
                TargetCode = equipment.Code + "SRC",
                DisplayName = "Disabled SQL rotation fixture",
                Kind = "EQUIPMENT",
                ConnectorType = "equipment-http",
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
                ApprovedEndpoint = "http://equipment-simulator:8080/equipment/" + equipment.Code,
                PollIntervalMs = 1000,
                TimeoutMs = 1000,
                StaleSec = 60
            });
            await db.SaveChangesAsync();
            var mapping = new EquipmentTagMapping
            {
                TargetId = source.TargetId,
                MetricCode = "TEMPERATURE",
                SourcePath = "temperature",
                DataType = "DECIMAL",
                ValueKind = "NUMBER",
                Unit = "C",
                Scale = 1
            };
            db.Add(mapping);
            await db.SaveChangesAsync();
            var stream = Guid.NewGuid().ToString();
            var prefix = Guid.NewGuid().ToString()[..24];
            var payload = "{}";
            var hash = CollectionFormat.Hash(new { });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO TELEMETRY_EVENT
                    (EVENT_ID,EQUIPMENT_ID,TARGET_ID,STREAM_ID,SEQUENCE_NO,SCHEMA_VERSION,
                     CONFIG_VERSION,OCCURRED_AT_UTC,RECEIVED_AT_UTC,PAYLOAD,PAYLOAD_HASH)
                SELECT {prefix} || LPAD(LEVEL,12,'0'), {equipment.EquipmentId}, {source.TargetId},
                    {stream}, LEVEL, 1, 1,
                    {start} + NUMTODSINTERVAL((LEVEL-1)*60,'SECOND'),
                    {start} + NUMTODSINTERVAL((LEVEL-1)*60,'SECOND'), {payload}, {hash}
                FROM DUAL CONNECT BY LEVEL <= 145
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO TELEMETRY_METRIC
                    (TELEMETRY_ID,METRIC_CODE,MAPPING_ID,NUMERIC_VALUE,UNIT,QUALITY)
                SELECT TELEMETRY_ID,'TEMPERATURE',{mapping.MappingId},25,'C','GOOD'
                FROM TELEMETRY_EVENT WHERE STREAM_ID = {stream}
                """);
        }
        async Task<ReportingDispatchResult> BatchAsync()
        {
            var result = await Read<ReportingDispatchResult>(
                await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
                200,
                "Run bounded scheduled batch"
            );
            Check(
                result.Examined is >= 0 and <= 20,
                "Each schedule call examines at most 20 new equipment"
            );
            return result;
        }
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
            403,
            "Wrong service cannot drain scheduled work"
        );
        await Expect(
            await admin.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
            404,
            "Public origin does not expose schedule controls"
        );

        var late = await EquipmentAsync("LATE");
        var fixtures = new List<Equipment>();
        // Publish the whole fixture atomically so the recovery trigger cannot see partial input.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            for (var number = 0; number < 27; number++)
            {
                var equipment = await EquipmentAsync(number.ToString("D2"), number != 0);
                fixtures.Add(equipment);
                await SeedAsync(equipment);
            }
            await transaction.CommitAsync();
        }
        Check(fixtures.Count == 27 && fixtures[0].EquipmentId > late.EquipmentId,
            "27 equipment, including a low-ID rejection, exceed the former 20 limit");
        var expectedIds = fixtures.Select(x => RequestId(x.EquipmentId)).ToArray();
        for (var pass = 0; pass < 15; pass++)
        {
            await BatchAsync();
            var count = await db.Set<IntegrationJob>().CountAsync(x => x
                .Principal == "SERVICE:jobs-worker"
                && x.Operation == "AI_ANALYZE" && expectedIds.Contains(x.RequestId));
            if (count == fixtures.Count) break;
        }
        var rows = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => x.Principal == "SERVICE:jobs-worker" && x.Operation == "AI_ANALYZE"
                && expectedIds.Contains(x.RequestId)).ToArrayAsync();
        Check(rows.Length == 27 && rows.Select(x => x.RequestId).Distinct().Count() == 27,
            "Every equipment beyond position 20 is admitted exactly once");
        var failure = rows.Single(x => x.RequestId == RequestId(fixtures[0].EquipmentId));
        Check(failure.State == "FAILED" && failure.SnapshotId is null,
            "Snapshot-less rejection is durable and does not starve higher equipment IDs");
        Check(await db.Set<AuditEvent>().AnyAsync(x => x.EntityId == failure.JobId
            && x.Action == "analysis.scheduled.rejected"),
                "Rejected equipment keeps an audit trail");
        await Wait(async () =>
        {
            var completed = await db.Set<IntegrationJob>().AsNoTracking()
                .Where(x => expectedIds.Contains(x.RequestId) && x
                    .Principal == "SERVICE:jobs-worker")
                .ToArrayAsync();
            if (completed.Any(x => x.State is "TIMED_OUT"))
                throw new InvalidOperationException("Rotation fixture timed out before dispatch");
            return completed.Count(x => x.State == "SUCCEEDED") == 26 ? completed : null;
        }, "26 valid equipment complete real Quartz/Gateway/Mock dispatch");
        var frozen = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => expectedIds.Contains(x.RequestId) && x.Principal == "SERVICE:jobs-worker")
            .Select(x => new {
                x.JobId,
                x.RequestId,
                x.State,
                x.SnapshotId,
                x.Attempt
            })
            .ToArrayAsync();
        var replayed = await Task.WhenAll(BatchAsync(), BatchAsync());
        Check(
            replayed.All(x => x.Examined == 0),
            "Concurrent replay skips all admitted jobs, including failures"
        );
        var after = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => expectedIds.Contains(x.RequestId) && x.Principal == "SERVICE:jobs-worker")
            .Select(x => new {
                x.JobId,
                x.RequestId,
                x.State,
                x.SnapshotId,
                x.Attempt
            })
            .ToArrayAsync();
        Check(frozen.OrderBy(x => x.JobId).SequenceEqual(after.OrderBy(x => x.JobId)),
            "Replayed scheduling never mutates terminal jobs, attempts, or snapshot references");
        await SeedAsync(late);
        var lateRequest = RequestId(late.EquipmentId);
        // Let the actual Quartz recovery trigger, rather than this test, admit the late lower ID.
        var lateJob = await Wait(async () =>
        {
            return await db.Set<IntegrationJob>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.RequestId == lateRequest && x.Principal == "SERVICE:jobs-worker");
        }, "Quartz recovery admits late data for an equipment below all previously processed IDs");
        await Wait(async () =>
        {
            var row = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x => x
                .JobId == lateJob.JobId);
            return row.State == "SUCCEEDED" ? row : null;
        }, "Automatically recovered late equipment completes Mock analysis");
        var snapshot = await db.Set<AnalysisSnapshot>().AsNoTracking()
            .SingleAsync(x => x.SnapshotId == lateJob.SnapshotId);
        Check(snapshot.PeriodStartUtc == start && snapshot.PeriodEndUtc == end,
            "Recovery keeps the fixed daily 24-hour window");
        var finalBatch = await BatchAsync();
        Check(finalBatch.Examined == 0, "Drained window remains empty after replay");
        var equipmentIds = fixtures.Select(x => x.EquipmentId).Append(late.EquipmentId).ToArray();
        Check(!await db.Set<Alarm>().AnyAsync(x => equipmentIds.Contains(x.EquipmentId)),
            "Rotation Mock results create no maintenance alarm");
        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/analysis-rotation-verification.json",
            JsonSerializer.Serialize(new
        {
            passed = checks.Count,
            checks,
            fixtureEquipment = 28,
            successfulMockJobs = 27,
            rejectedEquipment = 1,
            batchLimit = 20,
            selectedTarget = selected.TargetCode,
            periodEnd = end,
            equipmentIds,
            expectedRequests = expectedIds.Append(lateRequest).ToArray(),
            cloudApiCalls = 0,
            actualModelCalls = 0,
            note = "Direct isolated Oracle fixture, actual Quartz recovery and Mock dispatch."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Schedule rotation verification passed: " + checks.Count);
    }
}

