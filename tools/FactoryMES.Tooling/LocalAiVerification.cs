using System.Globalization;
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

// 고유 시험 설비의 실제 HTTP 수집 → 고정 스냅샷 → GPU 추론 → Oracle/알림 저장을 검증한다.
// 온도 offset20은 HIGH 경계 시연을 위한 명시적 시험 설정이며 원시 데이터는 직접 넣지 않는다.
public static class LocalAiVerification
{
    public static async Task RunAsync(MesDbContext db, Func<string, string> secret)
    {
        var checks = new List<string>();
        void Check(bool valid, string label)
        {
            if (!valid)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }

        async Task Phase(string stage)
        {
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/local-ai-stage.json", JsonSerializer.Serialize(
                new {
                    stage
                },
                new JsonSerializerOptions {
                    WriteIndented = true
                }
            ));
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until)
            {
                if (File.Exists("/reports/local-ai-ack.txt")
                    && (await File.ReadAllTextAsync("/reports/local-ai-ack.txt")).Trim() == stage)
                {
                    return;
                }
                await Task.Delay(250);
            }
            throw new TimeoutException("Local AI controller did not acknowledge " + stage);
        }
        // 외부 모델 없이도 정책 경계와 과거 직렬화 호환을 먼저 검사한다.
        var fixture = new AnalysisInput(
            1,
            "1",
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddMinutes(2),
            "1",
            2,
            [
                new AnalysisMetric(
                    "TEMPERATURE",
                    "C",
                    "25",
                    "35",
                    "30",
                    "35",
                    new AnalysisMetricObservation(
                        2,
                        "1",
                        DateTime.UnixEpoch.AddMinutes(2).AddSeconds(-1)
                    )
                )
            ],
            0,
            0
        );
        Check(MaintenanceRules.Evaluate(fixture).Risk == "HIGH", "35C boundary is HIGH");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with {
                    Last = "34.999999"
                }
            ]
        }).Risk == "MEDIUM", "Below35C is MEDIUM");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with {
                    Last = "30"
                }
            ]
        }).Risk == "MEDIUM", "30C boundary is MEDIUM");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with {
                    Last = "29.999999"
                }
            ]
        }).Risk == "LOW", "Below30C is LOW");
        Check(MaintenanceRules.Evaluate(fixture with {
            Coverage = "0.499999"
        }).Risk == "UNKNOWN",
            "Insufficient coverage is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with {
            SampleCount = 1
        }).Risk == "UNKNOWN",
            "Single sample is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with {
                    Unit = "F"
                }
            ]
        }).Risk == "UNKNOWN", "Unapproved temperature unit is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0],
                fixture.Metrics[0]
            ]
        }).Risk == "UNKNOWN", "Ambiguous temperature evidence is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with {
            Metrics = []
        }).Risk == "UNKNOWN",
            "Missing temperature is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with {
                    Observation = null
                }
            ]
        }).Risk == "UNKNOWN", "Absent per-metric observation is UNKNOWN");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with
                {
                    Observation = fixture.Metrics[0].Observation! with {
                        Coverage = "0.1"
                    }
                }
            ]
        }).Risk == "UNKNOWN", "Global coverage cannot hide sparse temperature coverage");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with
                {
                    Observation = fixture.Metrics[0].Observation! with {
                        SampleCount = 1
                    }
                }
            ]
        }).Risk == "UNKNOWN", "Global samples cannot hide single temperature sample");
        Check(MaintenanceRules.Evaluate(fixture with
        {
            Metrics = [
                fixture.Metrics[0] with
                {
                    Observation = fixture.Metrics[0].Observation! with
                    {
                        LastSampledAt = fixture.PeriodEnd.AddMinutes(-3)
                    }
                }
            ]
        }).Risk == "UNKNOWN", "Stale temperature cannot produce current risk");        var assessment = MaintenanceRules.Evaluate(fixture);
        Check(MaintenanceRules.Matches(fixture, assessment), "Recomputed assessment matches");
        Check(!MaintenanceRules.Matches(fixture, assessment with {
            Risk = "LOW"
        }),
            "Forged lower risk fails recomputation");
        Check(!MaintenanceRules.Matches(fixture, assessment with {
            Findings = [
                "forged"
            ]
        }),
            "Forged findings fail recomputation");
        var local = new AiSettings(
            "LOCAL",
            LocalAiPolicy.Endpoint,
            LocalAiPolicy.Model,
            null,
            LocalAiPolicy.TimeoutMs
        );
        Check(LocalAiPolicy.IsAllowed(local), "Only approved LOCAL configuration is accepted");
        Check(!LocalAiPolicy.IsAllowed(local with {
            ApprovedEndpoint = "http://example.com"
        }),
            "Arbitrary inference host rejected");
        Check(!LocalAiPolicy.IsAllowed(local with {
            Model = "qwen2.5:cloud"
        }),
            "Cloud model name rejected");
        Check(!LocalAiPolicy.IsAllowed(local with {
            CredentialRef = "any-key"
        }),
            "Credential activation rejected");
        Check(!LocalAiPolicy.IsAllowed(local with {
            TimeoutMs = 1
        }),
            "Unexpected timeout configuration rejected");
        Check(!LocalAiPolicy.IsAllowed(local with {
            ProviderKind = "CLOUD"
        }),
            "CLOUD provider rejected");
        var oldEvidence = new AnalysisEvidence(
            "old",
            [
                "one"
            ],
            [
                "two"
            ],
            "input",
            "config"
        );
        using var oldDocument = JsonDocument.Parse(
            "{\"summary\":\"old\",\"observations\":[\"one\"],\"recommendations\":[\"two\"],"
            + "\"inputHash\":\"input\",\"configHash\":\"config\"}"
        );
        Check(CollectionFormat.Hash(oldEvidence) == CollectionFormat.Hash(oldDocument.RootElement),
            "Absent maintenance field preserves old Mock canonical hash");

        var oldMetric = new AnalysisMetric(
            "TEMPERATURE",
            "C",
            "25",
            "35",
            "30",
            "35"
        );
        using var oldMetricDocument = JsonDocument.Parse(
            "{\"metricCode\":\"TEMPERATURE\",\"unit\":\"C\",\"minimum\":\"25\","
            + "\"maximum\":\"35\",\"mean\":\"30\",\"last\":\"35\"}"
        );
        Check(CollectionFormat.Hash(oldMetric) == CollectionFormat.Hash(oldMetricDocument.RootElement),
            "Absent per-metric observation preserves old Mock metric canonical hash");
        using var certificate = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client(string address) => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (
                _,
                peer,
                _,
                _
            ) => peer?.Thumbprint == certificate.Thumbprint
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(145)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var anonymous = Client("https://web:8443");
        using var jobs = Client("http://mes-backend:8080");

        async Task<HttpResponseMessage> Public(
            HttpClient client,
            string path,
            object body,
            bool put = false
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var message = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body, body.GetType())
            };
            message.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
            return await client.SendAsync(message);
        }

        async Task<T> Read<T>(
            HttpResponseMessage response,
            int status,
            string label
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == status,
                    label + " HTTP " + (int)response.StatusCode);
                return (await response.Content.ReadFromJsonAsync<T>())!;
            }
        }

        async Task Expect(
            HttpResponseMessage response,
            int status,
            string label
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == status,
                    label + " HTTP " + (int)response.StatusCode);
            }
        }

        async Task<T> Wait<T>(
            Func<Task<T?>> read,
            int seconds,
            string label
        ) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                var value = await read();
                if (value is not null)
                {
                    Check(true, label);
                    return value;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException(label);
        }

        await Read<SessionUser>(
            await Public(
                admin,
                "/api/v1/auth/login",
                new
                {
                    login = "admin",
                    password = secret("admin_password")
                }
            ),
            200,
            "Admin Cookie login"
        );
        await Read<SessionUser>(
            await Public(
                worker,
                "/api/v1/auth/login",
                new
                {
                    login = "portal",
                    password = secret("portal_password")
                }
            ),
            200,
            "Worker Cookie login"
        );
        using (var tokenResponse = await jobs.PostAsJsonAsync("/internal/v1/auth/token", new
        {
            clientId = "jobs-worker",
            clientSecret = secret("jobs-worker_client_secret"),
            audience = "mes-backend"
        }))
        {
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
            jobs.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", token.GetProperty("accessToken").GetString());
        }
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var create = new MockAiTargetCreate(
            "LAV" + suffix,
            "GPU maintenance verification",
            "LOCAL"
        );
        await Expect(
            await anonymous.GetAsync("/api/v1/analysis-options"),
            401,
            "Anonymous cannot read AI options"
        );
        await Expect(
            await Public(
                worker,
                "/api/v1/ai-targets",
                create
            ),
            403,
            "Worker cannot register LOCAL target"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/ai-targets", create),
            403,
            "LOCAL target registration requires CSRF"
        );
        await Expect(
            await Public(
                admin,
                "/api/v1/ai-targets",
                create with
                {
                    TargetId = "CLOUD" + suffix,
                    ProviderKind = "CLOUD"
                }
            ),
            422,
            "Paid provider cannot be registered"
        );
        var target = await Read<AiTargetView>(
            await Public(
                admin,
                "/api/v1/ai-targets",
                create
            ),
            201,
            "Register fixed LOCAL target"
        );
        Check(LocalAiPolicy.IsAllowed(target.Configuration.Content.Settings)
            && target.Configuration.Content.Settings.ProviderKind == "LOCAL",
            "Stored LOCAL target has approved endpoint and no credential");

        var line = new Line {
            Code = "LAL" + suffix,
            Name = "Local AI verification"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new Equipment
        {
            LineId = line.LineId,
            Code = "LAE" + suffix,
            Name = "Artificial offset HIGH fixture"
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        TargetView? connection = null;
        var aiRow = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetCode == target.TargetId);
        try
        {
            connection = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    new EquipmentTargetCreate(
                        "LAC" + suffix,
                        "Actual collection with explicit test offset20C",
                        "equipment-http",
                        true,
                        "EQUIPMENT",
                        equipment.EquipmentId.ToString(CultureInfo.InvariantCulture),
                        new EquipmentSettings(
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
                                    "20",
                                    true
                                )
                            ]
                        )
                    )
                ),
                201,
                "Register actual HTTP simulator input"
            );
            await Wait(
                async () =>
                {
                    var count = await db.Set<TelemetryEvent>().CountAsync(x =>
                        x.EquipmentId == equipment.EquipmentId);
                    return count >= 3 ? new object() : null;
                },
                80,
                "Actual connector persisted at least three GOOD inputs"
            );
            var options = await admin.GetFromJsonAsync<AnalysisOptions>("/api/v1/analysis-options");
            var end = options!.ServerTime;
            var request = new AnalysisRequest(
                Guid.NewGuid().ToString(),
                equipment.EquipmentId.ToString(CultureInfo.InvariantCulture),
                target.TargetId,
                end.AddMinutes(-2),
                end,
                "MAINTENANCE_RISK"
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/analyses",
                    request
                ),
                403,
                "Worker cannot queue maintenance analysis"
            );
            var queued = await Read<AnalysisJobView>(
                await Public(
                    admin,
                    "/api/v1/analyses",
                    request
                ),
                202,
                "Queue immutable LOCAL analysis"
            );
            var replay = await Read<AnalysisJobView>(
                await Public(
                    admin,
                    "/api/v1/analyses",
                    request
                ),
                202,
                "Replay same LOCAL request"
            );
            Check(queued.JobId == replay.JobId, "LOCAL request UUID remains idempotent");
            await Expect(
                await Public(
                    admin,
                    "/api/v1/analyses",
                    request with
                    {
                        AnalysisKind = "TREND_SUMMARY"
                    }
                ),
                409,
                "Changed LOCAL request intent conflicts"
            );
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var done = await Wait(
                async () =>
                {
                    var job = await admin.GetFromJsonAsync<AnalysisJobView>(
                        "/api/v1/analyses/" + queued.JobId);
                    if (job?.State is "FAILED" or "TIMED_OUT")
                    {
                        throw new InvalidOperationException("LOCAL analysis failed: " + job.ErrorCode);
                    }
                    return job?.State == "SUCCEEDED" ? job : null;
                },
                200,
                "Backend to Gateway to actual GPU Connector persisted success"
            );
            elapsed.Stop();
            var result = done.Results.Single();
            Check(result.Provider == "LOCAL" && result.Model == LocalAiPolicy.Model
                && !result.MockFlag && !result.Accepted,
                "Actual LOCAL result is distinct from Mock and still needs human review");
            Check(result.Risk == "HIGH" && result.Evidence.Maintenance?.Risk == "HIGH",
                "Actual offset temperature yields HIGH maintenance assessment");
            Check(!string.IsNullOrWhiteSpace(result.Evidence.Summary),
                "GPU model produced bounded nonempty narrative");
            Check(result.Evidence.InputHash == done.Snapshot!.InputHash
                && result.Evidence.ConfigHash == target.Configuration.ConfigHash,
                "LOCAL result binds immutable input and configuration hashes");
            var snapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                x.SnapshotId == long.Parse(done.Snapshot!.SnapshotId, CultureInfo.InvariantCulture));
            var input = JsonSerializer.Deserialize<AnalysisInput>(snapshot.Payload,
                CollectionFormat.Json)!;
            Check(CollectionFormat.Hash(input) == snapshot.InputHash && input.SampleCount >= 2,
                "Oracle snapshot hash matches actual collected input");
            var temperatureObservation = input.Metrics.Single(x =>
                x.MetricCode == "TEMPERATURE").Observation;
            Check(temperatureObservation is not null
                && temperatureObservation.SampleCount >= 2
                && decimal.Parse(temperatureObservation.Coverage,
                    CultureInfo.InvariantCulture) >= 0.5m
                && temperatureObservation.LastSampledAt.Kind == DateTimeKind.Utc
                && temperatureObservation.LastSampledAt >= input.PeriodEnd.AddMinutes(-2)
                && temperatureObservation.LastSampledAt < input.PeriodEnd,
                "Actual LOCAL snapshot preserves temperature-specific samples coverage and source time");            Check(MaintenanceRules.Matches(input, result.Evidence.Maintenance),
                "Stored maintenance grounds equal independent deterministic recomputation");
            var stored = await db.Set<AiAnalysisResult>().AsNoTracking().SingleAsync(x =>
                x.JobId == done.JobId);
            Check(stored.Provider == "LOCAL" && !stored.MockFlag && stored.Risk == "HIGH"
                && !stored.Accepted && stored.Model == result.Model,
                "Oracle row preserves LOCAL metadata and advisory risk");
            Check(await db.Set<Notification>().CountAsync(x =>
                x.AnalysisResultId == stored.ResultId) == 1,
                "One maintenance advisory notification is persisted");
            await Expect(
                await jobs.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
                    new DispatchBatchRequest(10)),
                200,
                "Re-dispatch completed jobs safely"
            );
            Check(await db.Set<AiAnalysisResult>().CountAsync(x => x.JobId == done.JobId) == 1,
                "Completed LOCAL job is not persisted twice");
            Check((await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                x.SnapshotId == snapshot.SnapshotId)).Payload == snapshot.Payload,
                "Continuing collection does not mutate input snapshot");
            Check(!await db.Set<Alarm>().AnyAsync(x => x.EquipmentId == equipment.EquipmentId),
                "Maintenance advisory does not automatically create process alarm or control");
            // 같은 수집 종료 시각에서 관측 기간만 넓혀 희소 입력을 만든다.
            // 표본0은 Backend에서 추론 전에 INSUFFICIENT_DATA로 끝나므로 실제 GPU UNKNOWN 시험과 구분한다.
            var sparseRequest = request with
            {
                RequestId = Guid.NewGuid().ToString(),
                PeriodStart = end.AddMinutes(-6)
            };
            var sparseQueued = await Read<AnalysisJobView>(
                await Public(
                    admin,
                    "/api/v1/analyses",
                    sparseRequest
                ),
                202,
                "Queue sparse actual-input LOCAL analysis"
            );
            var sparseDone = await Wait(
                async () =>
                {
                    var job = await admin.GetFromJsonAsync<AnalysisJobView>(
                        "/api/v1/analyses/" + sparseQueued.JobId);
                    if (job?.State is "FAILED" or "TIMED_OUT" or "INSUFFICIENT_DATA")
                    {
                        throw new InvalidOperationException("Sparse LOCAL analysis: "
                            + job.State + ":" + job.ErrorCode);
                    }
                    return job?.State == "SUCCEEDED" ? job : null;
                },
                200,
                "Actual GPU handles sparse input without inventing a risk"
            );
            var sparseResult = sparseDone.Results.Single();
            Check(sparseResult.Provider == "LOCAL" && !sparseResult.MockFlag
                && sparseResult.Risk == "UNKNOWN" && !sparseResult.Accepted,
                "Sparse real LOCAL result explicitly reports UNKNOWN");
            Check(sparseDone.JobId != done.JobId && sparseResult.ResultId != result.ResultId,
                "Second inference has distinct durable job and result");
            var sparseSnapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                x.SnapshotId == long.Parse(sparseDone.Snapshot!.SnapshotId,
                    CultureInfo.InvariantCulture));
            var sparseInput = JsonSerializer.Deserialize<AnalysisInput>(sparseSnapshot.Payload,
                CollectionFormat.Json)!;
            Check(sparseInput.SampleCount == input.SampleCount
                && decimal.Parse(sparseInput.Coverage, CultureInfo.InvariantCulture) < 0.5m,
                "Sparse window preserves actual samples and records insufficient policy coverage");
            Check(MaintenanceRules.Matches(sparseInput, sparseResult.Evidence.Maintenance),
                "UNKNOWN grounds independently recompute from sparse input");
            Check(await db.Set<Notification>().CountAsync(x =>
                x.AnalysisResultId == long.Parse(sparseResult.ResultId,
                    CultureInfo.InvariantCulture)) == 1,
                "Sparse LOCAL completion also preserves one advisory notification");
            var noDataRequest = request with
            {
                RequestId = Guid.NewGuid().ToString(),
                PeriodStart = end.AddHours(-2).AddMinutes(-2),
                PeriodEnd = end.AddHours(-2)
            };
            var noData = await Read<AnalysisJobView>(
                await Public(
                    admin,
                    "/api/v1/analyses",
                    noDataRequest
                ),
                202,
                "No-data LOCAL request is durably recorded"
            );
            Check(noData.State == "INSUFFICIENT_DATA" && noData.Results.Length == 0,
                "Zero input stops before inference and creates no invented analysis");
            var noDataSnapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                x.SnapshotId == long.Parse(noData.Snapshot!.SnapshotId,
                    CultureInfo.InvariantCulture));
            var noDataInput = JsonSerializer.Deserialize<AnalysisInput>(noDataSnapshot.Payload,
                CollectionFormat.Json)!;
            Check(noDataInput.SampleCount == 0 && noDataInput.Metrics.Length == 0,
                "No-data snapshot explicitly preserves zero samples");
            long? faultAttempt = null;
            string? faultJobId = null;
            if (Environment.GetEnvironmentVariable("MES_LOCAL_AI_FAULT_VERIFICATION") == "1")
            {
                // 외부 제어기가 모델 서버만 실제 중지한다. 실패 중 성공·Mock 결과를 만들면 안 된다.
                await Phase("stop-local-ai");
                var faultRequest = request with {
                    RequestId = Guid.NewGuid().ToString()
                };
                var faultQueued = await Read<AnalysisJobView>(
                    await Public(
                        admin,
                        "/api/v1/analyses",
                        faultRequest
                    ),
                    202,
                    "Queue LOCAL request while inference server is stopped"
                );
                faultJobId = faultQueued.JobId;
                var retry = await Wait(
                    async () =>
                    {
                        var row = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x =>
                            x.JobId == faultQueued.JobId);
                        if (row.State is "FAILED" or "TIMED_OUT" or "SUCCEEDED")
                        {
                            throw new InvalidOperationException("Unexpected stopped-server job: "
                                + row.State + ":" + row.Error);
                        }
                        return row.State == "QUEUED" && row.RetryAtUtc is not null
                            && row.Attempt >= 2 ? row : null;
                    },
                    80,
                    "Transport failure becomes durable queued retry"
                );
                Check(retry.Attempt == 2
                    && retry.Error is "ANALYSIS_TRANSPORT_FAILED" or "ANALYSIS_INPUT_UNAVAILABLE",
                    "First failed attempt records transient code and next attempt2");
                Check(!await db.Set<AiAnalysisResult>().AnyAsync(x => x.JobId == retry.JobId),
                    "Stopped inference creates neither successful result nor Mock fallback");
                var faultSnapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                    x.SnapshotId == retry.SnapshotId);
                var originalPayload = faultSnapshot.Payload;
                var originalHash = faultSnapshot.InputHash;
                await Phase("restart-local-ai");
                var recovered = await Wait(
                    async () =>
                    {
                        var job = await admin.GetFromJsonAsync<AnalysisJobView>(
                            "/api/v1/analyses/" + faultQueued.JobId);
                        if (job?.State is "FAILED" or "TIMED_OUT")
                        {
                            throw new InvalidOperationException("Recovered LOCAL failed: "
                                + job.State + ":" + job.ErrorCode);
                        }
                        return job?.State == "SUCCEEDED" ? job : null;
                    },
                    200,
                    "Actual inference server restart lets automatic retry succeed"
                );
                var recoveredResult = recovered.Results.Single();
                faultAttempt = recovered.Attempt;
                Check(recovered.Attempt >= 2 && recoveredResult.Provider == "LOCAL"
                    && !recoveredResult.MockFlag && !recoveredResult.Accepted,
                    "Recovered attempt remains actual LOCAL and human-reviewed advisory");
                Check(recovered.Snapshot!.SnapshotId == faultSnapshot.SnapshotId.ToString(
                    CultureInfo.InvariantCulture)
                    && recoveredResult.Evidence.InputHash == originalHash,
                    "Automatic retry preserves original snapshot identity and input hash");
                var afterRetry = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x =>
                    x.SnapshotId == faultSnapshot.SnapshotId);
                Check(afterRetry.Payload == originalPayload && afterRetry.InputHash == originalHash,
                    "Automatic retry does not rebuild immutable collected input");
                Check(await db.Set<AiAnalysisResult>().CountAsync(x => x.JobId == recovered.JobId) == 1,
                    "Recovered retry persists exactly one result");
                Check(await db.Set<Notification>().CountAsync(x =>
                    x.AnalysisResultId == long.Parse(recoveredResult.ResultId,
                        CultureInfo.InvariantCulture)) == 1,
                    "Recovered retry persists exactly one advisory notification");
            }
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/local-ai-verification.json", JsonSerializer.Serialize(
                new
                {
                    passed = checks.Count,
                    checks,
                    jobId = done.JobId,
                    resultId = result.ResultId,
                    unknownJobId = sparseDone.JobId,
                    unknownRisk = sparseResult.Risk,
                    unknownCoverage = sparseInput.Coverage,
                    sparseWindowMinutes = 6,
                    noDataJobId = noData.JobId,
                    noDataState = noData.State,
                    faultJobId,
                    faultAttempt,
                    provider = result.Provider,
                    model = result.Model,
                    risk = result.Risk,
                    elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    sampleCount = input.SampleCount,
                    coverage = input.Coverage,
                    actualGpuRequiredByConnector = true,
                    artificialFixtureOffsetCelsius = 20,
                    cloudApiCalls = 0,
                    externalDeliveryCalls = 0,
                    note = "Actual local LLM explanation plus deterministic demo policy; not trained failure prediction."
                },
                new JsonSerializerOptions {
                    WriteIndented = true
                }
            ));
            Console.WriteLine("Local AI verification passed: " + checks.Count);
        }
        finally
        {
            // 설정만 중지하고 원시 데이터·스냅샷·분석 이력은 다음 검토를 위해 보존한다.
            if (connection is not null)
            {
                var latest = await admin.GetFromJsonAsync<TargetView>(
                    "/api/v1/integration-targets/" + connection.TargetId);
                using var stopped = await Public(
                    admin,
                    "/api/v1/integration-targets/" + connection.TargetId + "/state",
                    new TargetStateUpdate(
                        latest!.Version,
                        latest.ConfigVersion,
                        false,
                        "Local AI verification complete"
                    ),
                    true
                );
                stopped.EnsureSuccessStatusCode();
            }
            aiRow.Enabled = false;
            await db.SaveChangesAsync();
        }
    }
}













