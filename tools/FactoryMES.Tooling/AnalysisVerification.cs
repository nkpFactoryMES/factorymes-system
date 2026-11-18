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

public static class AnalysisVerification
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
            object body,
            bool put = false
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post,
                route)
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
                if ((int)response.StatusCode != status)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var title = "Unexpected response";
                    try
                    {
                        using var problem = JsonDocument.Parse(body);
                        if (problem.RootElement.TryGetProperty("title", out var value))
                        {
                            title = value.GetString() ?? title;
                        }
                    }
                    catch (JsonException) { }
                    throw new InvalidOperationException(label + " HTTP "
                        + (int)response.StatusCode + ": " + title);
                }
                Check(true, label + " HTTP " + (int)response.StatusCode);
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
        await Expect(
            await anonymous.GetAsync("/api/v1/analysis-options"),
            401,
            "Anonymous AI options denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/analyses"),
            403,
            "Worker AI history denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/analysis-options"),
            403,
            "Worker AI options denied"
        );
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
                new DispatchBatchRequest(1)),
            403,
            "Wrong service dispatch denied"
        );
        await Expect(
            await admin.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
                new DispatchBatchRequest(1)),
            404,
            "AI internal paths not exposed by Web"
        );
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var create = new MockAiTargetCreate("AI" + suffix, "Local Mock analysis verification");
        await Expect(
            await worker.PostAsJsonAsync("/api/v1/ai-targets", create),
            403,
            "Worker target registration denied"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/ai-targets", create),
            403,
            "AI target registration needs CSRF"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/ai-targets",
                create with {
                    TargetId = "https://bad"
                }
            ),
            422,
            "Target id is not a URL"
        );
        var target = await Read<AiTargetView>(
            await Post(
                admin,
                "/api/v1/ai-targets",
                create
            ),
            201,
            "Register Mock AI target"
        );
        Check(target.Configuration.Content.Settings.ProviderKind == "MOCK"
            && target.Configuration.Content.Settings.ApprovedEndpoint is null
            && target.Configuration.Content.Settings.CredentialRef is null,
            "Target has no external endpoint or credential");
        await Expect(
            await Post(
                admin,
                "/api/v1/ai-targets",
                create
            ),
            409,
            "Duplicate target is a conflict"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/ai-targets",
                new
                {
                    targetId = "CLOUD" + suffix,
                    displayName = "Must not activate",
                    providerKind = "CLOUD"
                }
            ),
            422,
            "Paid-provider activation rejected by policy"
        );

        var line = new Line {
            Code = "AIL" + suffix,
            Name = "AI fixture"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new Equipment
        {
            LineId = line.LineId,
            Code = "AIE" + suffix,
            Name = "AI fixture"
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        var settings = new EquipmentSettings(
            "HTTP",
            "http://equipment-simulator:8080/equipment/" + equipment.Code,
            1000,
            1000,
            20,
            null,
            [
                new MappingInput(
                    null,
                    "TEMPERATURE",
                    "temperature",
                    "DECIMAL",
                    "NUMBER",
                    "C",
                    "1",
                    "0",
                    true
                )
            ]
        );
        var connection = await Read<TargetView>(
            await Post(
                admin,
                "/api/v1/integration-targets",
                new EquipmentTargetCreate(
                    "AIC" + suffix,
                    "Actual Simulator input for AI",
                    "equipment-http",
                    true,
                    "EQUIPMENT",
                    Id(equipment.EquipmentId),
                    settings
                )
            ),
            201,
            "Register actual Simulator collection"
        );
        var targetRow = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetCode == target
            .TargetId);
        try
        {
            await Wait(async () =>
            {
                var count = await db.Set<TelemetryEvent>().CountAsync(x => x
                    .EquipmentId == equipment.EquipmentId);
                return count >= 3 ? new object() : null;
            }, "Actual connector persists at least three input samples");
            var options = await admin.GetFromJsonAsync<AnalysisOptions>("/api/v1/analysis-options");
            var end = options!.ServerTime;
            var request = new AnalysisRequest(
                Guid.NewGuid().ToString(),
                Id(equipment.EquipmentId),
                target.TargetId,
                end.AddMinutes(-2),
                end,
                "TREND_SUMMARY"
            );
            await Expect(
                await worker.PostAsJsonAsync("/api/v1/analyses", request),
                403,
                "Worker analysis request denied"
            );
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/analyses", request),
                403,
                "Analysis request needs CSRF"
            );
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with {
                        RequestId = "bad"
                    }
                ),
                422,
                "Request UUID required"
            );
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with {
                        PeriodStart = end.AddDays(-2)
                    }
                ),
                422,
                "Analysis window bounded to 24h"
            );
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with {
                        PeriodEnd = end.AddHours(1)
                    }
                ),
                422,
                "Future analysis rejected"
            );
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with {
                        PeriodStart = DateTime.SpecifyKind(request.PeriodStart, DateTimeKind
                            .Unspecified)
                    }
                ),
                422,
                "Non-UTC analysis rejected"
            );
            var queued = await Read<AnalysisJobView>(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request
                ),
                202,
                "Queue real-input Mock analysis"
            );
            var replay = await Read<AnalysisJobView>(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request
                ),
                202,
                "Replay same analysis intent"
            );
            Check(queued.JobId == replay.JobId, "Analysis UUID is idempotent");
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with {
                        AnalysisKind = "MAINTENANCE_RISK"
                    }
                ),
                409,
                "Changed analysis intent rejected"
            );
            var done = await Wait(async () =>
            {
                var job = await admin
                    .GetFromJsonAsync<AnalysisJobView>("/api/v1/analyses/" + queued.JobId);
                if (job?.State is "FAILED" or "TIMED_OUT") throw new InvalidOperationException("Analysis failed: " + job.Error);
                return job?.State == "SUCCEEDED" ? job : null;
            }, "Quartz routes Backend to Gateway to AI Connector and persists result");
            var result = done.Results.Single();
            Check(result.Provider == "MOCK" && result.MockFlag && !result.Accepted && result
                .Risk == "UNKNOWN",
                "Mock result is never accepted as an actual maintenance prediction");
            Check(result.Evidence.InputHash == done.Snapshot!.InputHash
                && result.Evidence.ConfigHash == target.Configuration.ConfigHash
                && result.InputPeriodStart == request.PeriodStart && result
                    .InputPeriodEnd == request.PeriodEnd,
                "Result binds immutable input, settings and period");
            var snapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x => x
                .SnapshotId == long.Parse(done.Snapshot!.SnapshotId));
            var input = JsonSerializer.Deserialize<AnalysisInput>(snapshot.Payload,
                CollectionFormat.Json)!;
            Check(CollectionFormat.Hash(input) == snapshot.InputHash && input.SampleCount >= 2
                && input.Metrics.Length == 1,
                "Input snapshot contains actual GOOD metric statistics and matching hash");
            var payloadBefore = snapshot.Payload;
            await jobs.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
                new DispatchBatchRequest(10));
            Check(await db.Set<AiAnalysisResult>().CountAsync(x => x.JobId == done.JobId) == 1,
                "Completed analysis is not executed or persisted twice");
            Check(await db.Set<Notification>().CountAsync(x => x.AnalysisResultId == long
                .Parse(result.ResultId)) == 1,
                "One informational Mock completion notification is persisted");
            Check(!await db.Set<Alarm>().AnyAsync(x => x.EquipmentId == equipment.EquipmentId),
                "Mock result produces no real maintenance alarm");
            Check((await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x => x
                .SnapshotId == snapshot.SnapshotId)).Payload == payloadBefore,
                "New collection does not mutate existing analysis input");

            var emptyRequest = request with
            {
                RequestId = Guid.NewGuid().ToString(),
                PeriodStart = new DateTime(
                    2001,
                    1,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc
                ),
                PeriodEnd = new DateTime(
                    2001,
                    1,
                    1,
                    0,
                    2,
                    0,
                    DateTimeKind.Utc
                )
            };
            var empty = await Read<AnalysisJobView>(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    emptyRequest
                ),
                202,
                "No-data analysis request is recorded"
            );
            Check(empty.State == "INSUFFICIENT_DATA" && empty.Results.Length == 0,
                "No data is explicitly insufficient instead of a successful prediction");

            using var aiInput = Client("http://mes-backend:8080");
            using var connector = Client("http://ai-connector:8080");
            using var gateway = Client("http://integration-gateway:8080");
            async Task Service(
                HttpClient client,
                string identity,
                string audience
            )
            {
                using var response = await jobs.PostAsJsonAsync("/internal/v1/auth/token", new
                {
                    clientId = identity,
                    clientSecret = secret(identity + "_client_secret"),
                    audience
                });
                response.EnsureSuccessStatusCode();
                var token = await response.Content.ReadFromJsonAsync<JsonElement>();
                client.DefaultRequestHeaders
                    .Authorization = new AuthenticationHeaderValue("Bearer", token
                    .GetProperty("accessToken").GetString());
            }
            await Service(
                aiInput,
                "ai-connector",
                "mes-backend"
            );
            await Service(
                connector,
                "integration-gateway",
                "ai-connector"
            );
            await Service(
                gateway,
                "verification",
                "integration-gateway"
            );
            await Expect(
                await aiInput.GetAsync("/internal/v1/analysis-inputs/" + done.JobId + "?attempt=1"),
                422,
                "Completed job input is not an active execution"
            );
            var direct = await db.Set<IntegrationJob>().SingleAsync(x => x.JobId == empty.JobId);
            direct.State = "DISPATCHED";
            direct.DeadlineUtc = CollectionFormat.UtcNow().AddSeconds(30);
            await db.SaveChangesAsync();
            var envelope = await Read<AnalysisInputEnvelope>(
                await aiInput.GetAsync("/internal/v1/analysis-inputs/" + empty
                    .JobId + "?attempt=1"),
                200,
                "Authorized connector reads bound active input"
            );
            await Expect(
                await aiInput.GetAsync("/internal/v1/analysis-inputs/" + empty
                    .JobId + "?attempt=2"),
                422,
                "Wrong attempt cannot read input"
            );
            var dispatch = new AiDispatch(
                empty.JobId,
                empty.RequestId,
                1,
                "AI_ANALYZE",
                envelope.Snapshot,
                envelope.Configuration,
                direct.DeadlineUtc,
                empty.CorrelationId
            );
            await Expect(
                await gateway.PostAsJsonAsync("/internal/v1/ai/mock-executions", dispatch),
                403,
                "Gateway denies non-Backend principal"
            );
            await Expect(
                await wrong.GetAsync("/internal/v1/analysis-inputs/" + empty.JobId + "?attempt=1"),
                403,
                "Input rejects wrong service"
            );
            await Expect(
                await connector.PostAsJsonAsync("/internal/v1/ai/mock-executions", dispatch with {
                    Snapshot = dispatch.Snapshot with {
                        InputHash = new string('0', 64)
                    }
                }),
                422,
                "Connector rejects tampered input hash"
            );
            await Expect(
                await connector.PostAsJsonAsync("/internal/v1/ai/mock-executions", dispatch with {
                    Configuration = dispatch.Configuration with {
                        ConfigHash = new string('0', 64)
                    }
                }),
                422,
                "Connector rejects tampered configuration hash"
            );
            foreach (var provider in new[] {
                "CLOUD",
                "LOCAL"
            })
            {
                var content = dispatch.Configuration.Content with
                {
                    Settings = dispatch.Configuration.Content.Settings with {
                        ProviderKind = provider,
                        ApprovedEndpoint = "https://example.invalid"
                    }
                };
                await Expect(
                    await connector.PostAsJsonAsync("/internal/v1/ai/mock-executions", dispatch with
                    {
                        Configuration = new AiResolvedConfig(content, CollectionFormat
                            .Hash(content))
                    }),
                    422,
                    provider + " external execution is blocked without transport"
                );
            }
            await Expect(
                await connector.PostAsJsonAsync("/internal/v1/ai/mock-executions", dispatch with {
                    Deadline = CollectionFormat.UtcNow().AddSeconds(-1)
                }),
                422,
                "Connector rejects expired dispatch"
            );
            direct.DeadlineUtc = CollectionFormat.UtcNow().AddSeconds(-1);
            await db.SaveChangesAsync();
            await Wait(async () =>
            {
                var job = await admin
                    .GetFromJsonAsync<AnalysisJobView>("/api/v1/analyses/" + empty.JobId);
                return job?.State == "TIMED_OUT" ? job : null;
            }, "Quartz expires abandoned dispatched work");

            var server = (await admin
                .GetFromJsonAsync<AnalysisOptions>("/api/v1/analysis-options"))!.ServerTime;
            var scheduledEnd = DateTime.SpecifyKind(server.AddHours(9).Date.AddHours(-7),
                DateTimeKind.Utc);
            if (scheduledEnd > server) scheduledEnd = scheduledEnd.AddDays(-1);
            var sourceTarget = await db.Set<IntegrationTarget>().SingleAsync(x => x
                .TargetCode == connection.TargetId);
            var sourceMapping = await db.Set<EquipmentTagMapping>().SingleAsync(x => x
                .TargetId == sourceTarget.TargetId);
            // A direct Oracle fixture preserves every sample while testing sparse coverage.
            var stream = Guid.NewGuid().ToString();
            var eventPrefix = Guid.NewGuid().ToString()[..24];
            var time = scheduledEnd.AddHours(-1);
            var payloadHash = CollectionFormat.Hash(new { });
            var emptyPayload = "{}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO TELEMETRY_EVENT
                    (EVENT_ID, EQUIPMENT_ID, TARGET_ID, STREAM_ID, SEQUENCE_NO,
                     SCHEMA_VERSION, CONFIG_VERSION, OCCURRED_AT_UTC, RECEIVED_AT_UTC,
                     PAYLOAD, PAYLOAD_HASH)
                SELECT {eventPrefix} || LPAD(LEVEL, 12, '0'), {equipment.EquipmentId},
                    {sourceTarget.TargetId}, {stream}, LEVEL, 1, {sourceTarget.ConfigVersion},
                    {time}, {time}, {emptyPayload}, {payloadHash}
                FROM DUAL CONNECT BY LEVEL <= 10001
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO TELEMETRY_METRIC
                    (TELEMETRY_ID, METRIC_CODE, MAPPING_ID, NUMERIC_VALUE, UNIT, QUALITY)
                SELECT TELEMETRY_ID, {sourceMapping.MetricCode}, {sourceMapping.MappingId},
                    25, 'C', 'GOOD'
                FROM TELEMETRY_EVENT WHERE STREAM_ID = {stream}
                """);
            await Expect(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request with
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        PeriodStart = scheduledEnd.AddDays(-1),
                        PeriodEnd = scheduledEnd
                    }
                ),
                202,
                "Large sparse input is accepted without silent truncation"
            );
            var scheduledTarget = await (
                from row in db.Set<IntegrationTarget>()
                join config in db.Set<AiProviderConfig>() on row.TargetId equals config.TargetId
                where row.Kind == "AI" && row.Enabled == EF.Parameter(true)
                    && row.Deleted == EF.Parameter(false) && config.ProviderKind == "MOCK"
                orderby row.TargetId
                select row.TargetCode
            ).FirstAsync();
            var scheduleKey = System.Text.Encoding.UTF8.GetBytes(scheduledTarget + ":"
                + equipment.EquipmentId + ":" + scheduledEnd.Ticks);
            var scheduleId = new Guid(System.Security.Cryptography.SHA256.HashData(scheduleKey)
                .AsSpan(0, 16)).ToString();
            var scheduled = await Read<ReportingDispatchResult>(
                await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
                200,
                "Authorized scheduled-analysis entry point"
            );
            Check(
                scheduled.Examined is >= 0 and <= 20,
                "Scheduled batch includes prior-window fixture and is bounded"
            );
            var rejected = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x =>
                x.RequestId == scheduleId && x.Principal == "SERVICE:jobs-worker"
                && x.Operation == "AI_ANALYZE");
            Check(rejected.State == "INSUFFICIENT_DATA" && rejected.SnapshotId is not null,
                "Large sparse scheduled input retains its snapshot and insufficient coverage state");
            Check(!await db.Set<AuditEvent>().AnyAsync(x => x.EntityId == rejected.JobId
                && x.Action == "analysis.scheduled.rejected"),
                    "Large input is not rejected by the former raw row limit");
            var scheduledCount = await db.Set<IntegrationJob>().CountAsync(x => x
                .Principal == "SERVICE:jobs-worker" && x.Operation == "AI_ANALYZE");
            await Read<ReportingDispatchResult>(
                await jobs.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
                200,
                "Scheduled execution can be replayed"
            );
            Check(await db.Set<IntegrationJob>().CountAsync(x => x
                .Principal == "SERVICE:jobs-worker" && x
                .Operation == "AI_ANALYZE") == scheduledCount,
                "Same scheduled window creates no duplicate analysis requests");

            var page = await admin
                .GetFromJsonAsync<PageResult<AnalysisJobView>>("/api/v1/analyses?limit=1");
            Check(
                page!.Items.Length == 1 && page.NextCursor is not null,
                "Analysis history cursor is bounded"
            );
            var next = await admin
                .GetFromJsonAsync<PageResult<AnalysisJobView>>("/api/v1/analyses?limit=1&cursor=" + Uri.EscapeDataString(page.NextCursor!));
            Check(
                next!.Items.Length == 1 && next.Items[0].JobId != page.Items[0].JobId,
                "Analysis history cursor advances without duplicate row"
            );
            targetRow.Enabled = false;
            await db.SaveChangesAsync();
            var disabledReplay = await Read<AnalysisJobView>(
                await Post(
                    admin,
                    "/api/v1/analyses",
                    request
                ),
                202,
                "Replay after target disable returns original job"
            );
            Check(
                disabledReplay.JobId == done.JobId,
                "Disabling target does not break request idempotence"
            );
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/analyses-verification.json", JsonSerializer
                .Serialize(new
            {
                passed = checks.Count,
                checks,
                successJobId = done.JobId,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0,
                note = "Actual Simulator input; Mock risk UNKNOWN/accepted false, not a real prediction."
            }, new JsonSerializerOptions {
                WriteIndented = true
            }));
            Console.WriteLine("Analysis verification passed: " + checks.Count);
        }
        finally
        {
            var latest = await admin
                .GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + connection.TargetId);
            using var stopped = await Post(
                admin,
                "/api/v1/integration-targets/" + connection.TargetId + "/state",
                new TargetStateUpdate(
                    latest!.Version,
                    latest.ConfigVersion,
                    false,
                    "AI verification complete"
                ),
                true
            );
            stopped.EnsureSuccessStatusCode();
            targetRow.Enabled = false;
            await db.SaveChangesAsync();
        }
    }

    private static string Id(long value) => value.ToString(System.Globalization.CultureInfo
        .InvariantCulture);
}




