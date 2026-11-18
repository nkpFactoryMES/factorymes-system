using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class AnalysisRecoveryVerification
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
        await Read<SessionUser>(
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
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
            403,
            "Unauthorized service cannot request recovery"
        );
        var stage = "/reports/analysis-recovery-stage.json";
        var ack = "/reports/analysis-recovery-ack.txt";
        async Task Signal(string value)
        {
            await File.WriteAllTextAsync(stage, JsonSerializer.Serialize(new {
                stage = value
            }));
            if (value != "done")
            {
                await Wait(async () => File.Exists(ack) && await File.ReadAllTextAsync(ack) == value
                    ? new object() : null, "Controller acknowledges " + value);
            }
        }
        async Task<ReportingDispatchResult> Schedule()
        {
            using var response = await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled",
                new { });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ReportingDispatchResult>())!;
        }
        // Drain retained legacy input before targeted network faults, with the provider available.
        for (var batch = 0; batch < 100; batch++)
        {
            if ((await Schedule()).Examined == 0) break;
        }
        await Wait(async () => await db.Set<IntegrationJob>().CountAsync(x => x
            .Operation == "AI_ANALYZE"
            && (x.State == "QUEUED" || x.State == "DISPATCHED")) == 0 ? new object() : null,
            "Existing AI backlog drains before isolated fault injection");
        var key = "RC" + Guid.NewGuid().ToString("N")[..10];
        var ai = await Read<AiTargetView>(
            await Post(
                admin,
                "/api/v1/ai-targets",
                new MockAiTargetCreate(key + "AI", "Recovery verification")
            ),
            201,
            "Create isolated Mock target"
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
        var line = new Line {
            Code = key,
            Name = "Recovery isolated fixture"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new List<Equipment>();
        var requests = new List<string>();
        await Signal("stop-first");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            for (var number = 0; number < 2; number++)
            {
                var item = new Equipment
                {
                    LineId = line.LineId,
                    Code = key + number,
                    Name = "Recovery equipment " + number
                };
                db.Add(item);
                await db.SaveChangesAsync();
                equipment.Add(item);
                var source = new IntegrationTarget
                {
                    TargetCode = item.Code + "SRC",
                    Kind = "EQUIPMENT",
                    ConnectorType = "equipment-http",
                    DisplayName = "Disabled historical fixture",
                    EquipmentId = item.EquipmentId,
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
                    ApprovedEndpoint = "http://equipment-simulator:8080/equipment/" + item.Code,
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
                for (var days = 2; days <= 4; days++)
                {
                    var windowEnd = end.AddDays(-days);
                    var windowStart = windowEnd.AddDays(-1);
                    var identity = System.Text.Encoding.UTF8.GetBytes(selected.TargetCode + ":"
                        + item.EquipmentId + ":" + windowEnd.Ticks);
                    requests.Add(new Guid(System.Security.Cryptography.SHA256.HashData(identity)
                        .AsSpan(0, 16)).ToString());
                    var stream = Guid.NewGuid().ToString();
                    var prefix = Guid.NewGuid().ToString()[..24];
                    var payload = "{}";
                    var hash = CollectionFormat.Hash(new { });
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO TELEMETRY_EVENT
                            (EVENT_ID,EQUIPMENT_ID,TARGET_ID,STREAM_ID,SEQUENCE_NO,SCHEMA_VERSION,
                             CONFIG_VERSION,OCCURRED_AT_UTC,RECEIVED_AT_UTC,PAYLOAD,PAYLOAD_HASH)
                        SELECT {prefix} || LPAD(LEVEL,12,'0'), {item.EquipmentId}, {source
                            .TargetId},
                            {stream}, LEVEL, 1, 1,
                            {windowStart} + NUMTODSINTERVAL((LEVEL-1)*60,'SECOND'),
                            {windowStart} + NUMTODSINTERVAL((LEVEL-1)*60,'SECOND'), {payload},
                                {hash}
                        FROM DUAL CONNECT BY LEVEL <= 145
                        """);
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO TELEMETRY_METRIC
                            (TELEMETRY_ID,METRIC_CODE,MAPPING_ID,NUMERIC_VALUE,UNIT,QUALITY)
                        SELECT TELEMETRY_ID,'TEMPERATURE',{mapping.MappingId},25,'C','GOOD'
                        FROM TELEMETRY_EVENT WHERE STREAM_ID = {stream}
                        """);
                }
            }
            await transaction.CommitAsync();
        }
        for (var batch = 0; batch < 10; batch++)
        {
            var result = await Schedule();
            Check(result.Examined <= 20, "Historical backfill keeps the 20-pair batch limit");
            if (await db.Set<IntegrationJob>().CountAsync(x => requests.Contains(x.RequestId)
                && x.Principal == "SERVICE:jobs-worker") == 6) break;
        }
        Check(await db.Set<IntegrationJob>().CountAsync(x => requests.Contains(x.RequestId)
            && x.Principal == "SERVICE:jobs-worker") == 6,
            "Two equipment across three missed historical days are admitted exactly once");
        var manualRequest = new AnalysisRequest(
            Guid.NewGuid().ToString(),
            equipment[0].EquipmentId.ToString(),
            ai.TargetId,
            end.AddDays(-5),
            end.AddDays(-4),
            "TREND_SUMMARY"
        );
        var manual = await Read<AnalysisJobView>(await Post(admin, "/api/v1/analyses",
            manualRequest),
            202, "Queue manual completion-notification deduplication fixture");
        var original = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => requests.Contains(x.RequestId) && x.Principal == "SERVICE:jobs-worker")
            .ToArrayAsync();
        await Wait(async () => await db.Set<IntegrationJob>().AsNoTracking()
            .FirstOrDefaultAsync(x =>
            requests.Contains(x.RequestId) && x.State == "QUEUED" && x.Attempt == 2
                && x.RetryAtUtc != null), "Real gateway outage persists a delayed retry");
        await Signal("restart-recover");
        await Wait(async () => await db.Set<IntegrationJob>().CountAsync(x => requests.Contains(x
            .RequestId)
            && x.State == "SUCCEEDED") == 6 ? new object() : null,
            "Backend restart and restored gateway complete all historical Mock jobs");
        var finished = await db.Set<IntegrationJob>().AsNoTracking()
            .Where(x => requests.Contains(x.RequestId) && x.Principal == "SERVICE:jobs-worker")
            .ToArrayAsync();
        Check(finished.All(x => original.Any(y => y.JobId == x.JobId && y.SnapshotId == x
            .SnapshotId)),
            "Retries preserve job identities and immutable snapshots");
        Check(finished.Any(x => x.Attempt > 1) && finished.All(x => x.Attempt <= 3 && x
            .RetryAtUtc == null),
            "Automatic recovery is bounded and clears due timestamps after claim");
        var ids = finished.Select(x => x.JobId).ToArray();
        Check(await db.Set<AiAnalysisResult>().CountAsync(x => ids.Contains(x.JobId)) == 6,
            "Each historical job has exactly one persisted analysis result");
        await Schedule();
        await Schedule();
        Check(await db.Set<IntegrationJob>().CountAsync(x => requests.Contains(x.RequestId)
            && x.Principal == "SERVICE:jobs-worker") == 6,
            "Repeated backfill does not duplicate historical jobs");
        Check(!await db.Set<AnalysisSnapshot>().AnyAsync(x => equipment.Select(e => e.EquipmentId)
            .Contains(x.EquipmentId) && x.PeriodEndUtc > end.AddDays(-2)),
            "Empty days are not invented and historical windows are not rewritten to today");
        await Wait(async () => await db.Set<IntegrationJob>().AsNoTracking()
            .FirstOrDefaultAsync(x =>
            x.JobId == manual.JobId && x.State == "SUCCEEDED"),
                "Manual job also completes after service recovery");
        var duplicate = await Read<AnalysisJobView>(await Post(admin, "/api/v1/analyses",
            manualRequest),
            202, "Replay completed manual request");
        Check(duplicate.JobId == manual.JobId && duplicate.Results.Length == 1,
            "Manual replay retains one job and one result");
        var manualResult = await db.Set<AiAnalysisResult>().SingleAsync(x => x.JobId == manual
            .JobId);
        Check(await db.Set<Notification>().CountAsync(x => x.AnalysisResultId == manualResult
            .ResultId) == 1,
            "Recovered completion creates exactly one informational notification");
        await Signal("stop-exhaust");
        var failRequest = new AnalysisRequest(
            Guid.NewGuid().ToString(),
            equipment[0].EquipmentId.ToString(),
            ai.TargetId,
            end.AddDays(-5),
            end.AddDays(-4),
            "TREND_SUMMARY"
        );
        var exhausted = await Read<AnalysisJobView>(
            await Post(
                admin,
                "/api/v1/analyses",
                failRequest
            ),
            202,
            "Queue bounded persistent-outage fixture"
        );
        await Wait(async () => await db.Set<IntegrationJob>().AsNoTracking()
            .FirstOrDefaultAsync(x =>
            x.JobId == exhausted.JobId && x.State == "FAILED" && x.Attempt == 3),
            "Continuous transport outage stops after three attempts");
        await Signal("restore-exhaust");
        var replay = await Read<AnalysisJobView>(
            await Post(
                admin,
                "/api/v1/analyses",
                failRequest
            ),
            202,
            "Replay exhausted intent"
        );
        Check(replay.JobId == exhausted.JobId && replay.State == "FAILED" && replay.Attempt == 3,
            "User replay cannot bypass exhausted retry budget");
        Check(!await db.Set<AiAnalysisResult>().AnyAsync(x => x.JobId == exhausted.JobId),
            "Exhausted attempts persist no fabricated result");
        Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityId == exhausted.JobId
            && x.Action == "analysis.retry.scheduled") == 2,
            "Both delayed retries have durable audit history");
        await Signal("pause-worker");
        var invalid = await Read<AnalysisJobView>(
            await Post(
                admin,
                "/api/v1/analyses",
                failRequest with {
                    RequestId = Guid.NewGuid().ToString()
                }
            ),
            202,
            "Queue permanent validation fixture"
        );
        var ownTarget = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetCode == ai
            .TargetId);
        ownTarget.ConfigVersion++;
        await db.SaveChangesAsync();
        using var dispatch = await jobs.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
            new DispatchBatchRequest(100));
        dispatch.EnsureSuccessStatusCode();
        var rejected = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x => x
            .JobId == invalid.JobId);
        Check(rejected.State == "FAILED" && rejected.Attempt == 1 && rejected.RetryAtUtc == null,
            "Permanent configuration mismatch is never automatically retried");
        Check(!await db.Set<AuditEvent>().AnyAsync(x => x.EntityId == invalid.JobId
            && x.Action == "analysis.retry.scheduled"), "Permanent rejection has no retry audit");
        ownTarget.Enabled = false;
        await db.SaveChangesAsync();
        await Signal("done");
        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/analysis-recovery-verification.json",
            JsonSerializer.Serialize(new
        {
            passed = checks.Count,
            checks,
            historicalWindows = 3,
            historicalEquipment = 2,
            recoveredJobs = ids,
            exhaustedJobId = exhausted.JobId,
            permanentJobId = invalid.JobId,
            cloudApiCalls = 0,
            actualModelCalls = 0,
            note = "Actual Oracle/Quartz/Gateway faults and backend restart; direct historical SQL fixtures."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Analysis recovery verification passed: " + checks.Count);
    }
}

