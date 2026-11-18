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

public static class AnalysisInputRetryVerification
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
            await wrong.PostAsJsonAsync("/internal/v1/analyses/scheduled", new { }),
            403,
            "Unauthorized service cannot request recovery"
        );
        using var sourceReport = JsonDocument.Parse(await File.ReadAllTextAsync(
            "/reports/analysis-recovery-verification.json"));
        var sourceId = sourceReport.RootElement.GetProperty("recoveredJobs")[0].GetString();
        var sourceJob = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x => x
            .JobId == sourceId);
        var source = await db.Set<AnalysisSnapshot>().AsNoTracking()
            .SingleAsync(x => x.SnapshotId == sourceJob.SnapshotId);
        var key = "IR" + Guid.NewGuid().ToString("N")[..10];
        var ai = await Read<AiTargetView>(
            await Post(
                admin,
                "/api/v1/ai-targets",
                new MockAiTargetCreate(key, "Input retry fixture")
            ),
            201,
            "Create isolated input retry target"
        );
        var target = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetCode == key);
        async Task<IntegrationJob> SeedAsync()
        {
            var request = new AnalysisRequest(
                Guid.NewGuid().ToString(),
                source.EquipmentId.ToString(),
                key,
                source.PeriodStartUtc,
                source.PeriodEndUtc,
                "TREND_SUMMARY"
            );
            var due = CollectionFormat.UtcNow().AddSeconds(5);
            var job = new IntegrationJob
            {
                JobId = Guid.NewGuid().ToString(),
                RequestId = request.RequestId,
                TargetId = target.TargetId,
                ActorId = long.Parse(me.UserId),
                Principal = "USER:" + me.UserId,
                Operation = "AI_ANALYZE",
                ConfigVersion = 1,
                Attempt = 2,
                State = "QUEUED",
                Error = "ANALYSIS_INPUT_UNAVAILABLE",
                RetryAtUtc = due,
                DeadlineUtc = due.AddSeconds(30),
                CorrelationId = request.RequestId
            };
            db.Add(job);
            db.Add(new AuditEvent
            {
                ActorId = job.ActorId,
                Principal = job.Principal,
                Action = "analysis.requested",
                EntityType = "INTEGRATION_JOB",
                EntityId = job.JobId,
                Detail = JsonSerializer.Serialize(new {
                    request,
                    configuration = ai.Configuration
                }, CollectionFormat.Json),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync();
            return job;
        }
        var pending = await SeedAsync();
        using var early = await jobs.PostAsJsonAsync("/internal/v1/analyses/dispatch-batch",
            new DispatchBatchRequest(100));
        early.EnsureSuccessStatusCode();
        var waiting = await db.Set<IntegrationJob>().AsNoTracking().SingleAsync(x => x
            .JobId == pending.JobId);
        Check(waiting.SnapshotId is null && waiting.Attempt == 2 && waiting.State == "QUEUED",
            "Persisted snapshot-less input retry is not claimed before its due time");
        var done = await Wait(async () => await db.Set<IntegrationJob>().AsNoTracking()
            .FirstOrDefaultAsync(x =>
            x.JobId == pending.JobId && x.State == "SUCCEEDED"),
            "Quartz recaptures missing input and completes the prepared retry");
        Check(done.Attempt == 2 && done.RetryAtUtc is null && done.SnapshotId is not null,
            "Input capture fills the missing snapshot without resetting attempts");
        var snapshot = await db.Set<AnalysisSnapshot>().AsNoTracking().SingleAsync(x => x
            .SnapshotId == done.SnapshotId);
        Check(snapshot.InputHash == source.InputHash && snapshot.PeriodStartUtc == source
            .PeriodStartUtc
            && snapshot.PeriodEndUtc == source.PeriodEndUtc,
            "Recapture uses the intended historical window and canonical input hash");
        Check(await db.Set<AiAnalysisResult>().CountAsync(x => x.JobId == done.JobId) == 1,
            "Recovered admission has one result");
        var invalid = await SeedAsync();
        target.ConfigVersion++;
        await db.SaveChangesAsync();
        var rejected = await Wait(async () => await db.Set<IntegrationJob>().AsNoTracking()
            .FirstOrDefaultAsync(x =>
            x.JobId == invalid.JobId && x.State == "FAILED"),
            "Frozen configuration mismatch permanently rejects recapture");
        Check(rejected.Attempt == 2 && rejected.SnapshotId is null && rejected.RetryAtUtc is null,
            "Permanent input failure clears due time without scheduling a third attempt");
        target.Enabled = false;
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync("/reports/analysis-input-retry-verification.json",
            JsonSerializer.Serialize(new
        {
            passed = checks.Count,
            checks,
            successfulJob = done.JobId,
            rejectedJob = invalid.JobId,
            cloudApiCalls = 0,
            note = "Prepared snapshot-less Oracle retry; actual Quartz/Mock execution, not a DB disconnect."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Input retry verification passed: " + checks.Count);
    }
}

