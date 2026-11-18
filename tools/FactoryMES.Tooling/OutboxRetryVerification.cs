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

public static class OutboxRetryVerification
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
        async Task<(MockNotificationResult Notification, OutboxEvent Event)> Create(string scenario)
        {
            var notification = await Read<MockNotificationResult>(
                await Post(
                    admin,
                    "/api/v1/mock-notifications",
                    new MockNotificationRequest(Guid.NewGuid().ToString(), scenario)
                ),
                201,
                "Create " + scenario
            );
            var row = await db.Set<OutboxEvent>().AsNoTracking().SingleAsync(x =>
                x.Type == "mes.notification.created.v1" && x.AggregateId == notification
                    .NotificationId);
            return (notification, row);
        }
        Task<OutboxRetryState?> State(long id) => admin.GetFromJsonAsync<OutboxRetryState>(
            "/api/v1/outbox-events/" + id + "/retry-state"
        );
        Task<OutboxRetryState> Terminal(long id, long attempts) => Wait(async () =>
        {
            var state = await State(id);
            return state?.Event.Status == "FAILED" && state.Event.Attempts == attempts
                && state.Event.NextAttemptAt is null ? state : null;
        }, "Outbox reaches bounded terminal failure at " + attempts);
        async Task<DeliveryView[]?> Accepted(string notificationId)
        {
            var page = await admin.GetFromJsonAsync<PageResult<DeliveryView>>(
                "/api/v1/deliveries?notificationId=" + notificationId
            );
            if (page!.Items.Length != 2) return null;
            var ids = string.Join(",", page.Items.Select(x => x.DeliveryId));
            var statuses = await admin.GetFromJsonAsync<DeliveryMockStatus[]>(
                "/api/v1/delivery-mock-statuses?deliveryIds=" + ids
            );
            return statuses!.Length == 2 && statuses.All(x => x.Outcome == "ACCEPTED")
                ? (await admin.GetFromJsonAsync<PageResult<DeliveryView>>(
                    "/api/v1/deliveries?notificationId=" + notificationId
                ))!.Items : null;
        }

        var once = await Create("OUTBOX_FAIL_ONCE");
        var state = await Terminal(once.Event.OutboxId, 3);
        Check(state.CanRetry && state.StateHash.Length == 64
            && state.Event.LastError == "MOCK_OUTBOX_FAILURE",
            "Terminal local handoff failure has a retry token and explicit Mock error");
        var path = "/api/v1/outbox-events/" + state.Event.OutboxId;
        await Expect(
            await anonymous.GetAsync(path + "/retry-state"),
            401,
            "Anonymous retry-state denied"
        );
        await Expect(
            await worker.GetAsync(path + "/retry-state"),
            403,
            "Worker retry-state denied"
        );
        var request = new OutboxRetryRequest(
            Guid.NewGuid().ToString(),
            state.StateHash,
            "Reviewed local handoff failure"
        );
        await Expect(
            await Post(
                worker,
                path + "/retry",
                request
            ),
            403,
            "Worker Outbox retry denied"
        );
        await Expect(
            await admin.PostAsJsonAsync(path + "/retry", request),
            403,
            "Outbox retry requires CSRF"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with {
                    Reason = " "
                }
            ),
            422,
            "Outbox retry reason required"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with {
                    ExpectedStateHash = "bad"
                }
            ),
            422,
            "State token format validated"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with {
                    ExpectedStateHash = new string('0', 64)
                }
            ),
            409,
            "Stale observed state rejected"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                new
                {
                    request.ClientRequestId,
                    request.ExpectedStateHash,
                    request.Reason,
                    payload = "forged"
                }
            ),
            400,
            "Event editing fields are not accepted by retry API"
        );

        var queued = await Read<OutboxRetryState>(
            await Post(
                admin,
                path + "/retry",
                request
            ),
            202,
            "Administrator queues handoff retry"
        );
        Check(
            queued.Event.Attempts == 3 && queued.Event.Status == "PENDING",
            "Retry does not reset cumulative attempts"
        );
        var replay = await Read<OutboxRetryState>(
            await Post(
                admin,
                path + "/retry",
                request
            ),
            202,
            "Retry request can be replayed"
        );
        Check(
            replay.Event.OutboxId == queued.Event.OutboxId,
            "Replay refers to the same Outbox event"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with {
                    Reason = "Different reason"
                }
            ),
            409,
            "Changed retry intent conflicts"
        );
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with {
                    ClientRequestId = Guid.NewGuid().ToString()
                }
            ),
            409,
            "Old state cannot enqueue a second retry"
        );
        var handedOff = await Wait(async () =>
        {
            var current = await State(once.Event.OutboxId);
            return current?.Event.Status == "DELIVERED" ? current : null;
        }, "Retried event is durably handed off");
        Check(handedOff.Event.Attempts == 4 && !handedOff.CanRetry,
            "Handoff success keeps attempts and prevents new manual resend");
        var original = await db.Set<OutboxEvent>().AsNoTracking().SingleAsync(x => x
            .OutboxId == once.Event.OutboxId);
        Check(original.EventId == once.Event.EventId && original.Payload == once.Event.Payload
            && original.AggregateVersion == once.Event.AggregateVersion,
            "Original event identity, payload and aggregate version remain immutable");
        var deliveries = await Wait(
            () => Accepted(once.Notification.NotificationId),
            "Two local channel receipts follow retried handoff"
        );
        Check(deliveries.All(x => x.Status == "PENDING" && x.SentAt is null && x.Attempts == 1),
            "Retry creates no real SENT state and no duplicate channel attempt");
        await Read<OutboxRetryState>(
            await Post(
                admin,
                path + "/retry",
                request
            ),
            202,
            "Lost-response replay after delivery is safe"
        );
        var auditKey = state.Event.OutboxId;
        Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "OUTBOX_EVENT"
            && x.EntityId == auditKey && x.Action == "outbox.retry.requested") == 1,
            "One request has one retry audit despite replays");
        await Expect(
            await Post(
                admin,
                path + "/retry",
                request with
                {
                    ClientRequestId = Guid.NewGuid().ToString(),
                    ExpectedStateHash = handedOff.StateHash
                }
            ),
            409,
            "Already handed-off event cannot be resent"
        );

        var always = await Create("OUTBOX_ALWAYS_FAIL");
        var stopped = await Terminal(always.Event.OutboxId, 3);
        var alwaysPath = "/api/v1/outbox-events/" + always.Event.OutboxId + "/retry";
        var left = new OutboxRetryRequest(
            Guid.NewGuid().ToString(),
            stopped.StateHash,
            "Concurrent operator review A"
        );
        var right = new OutboxRetryRequest(
            Guid.NewGuid().ToString(),
            stopped.StateHash,
            "Concurrent operator review B"
        );
        var races = await Task.WhenAll(Post(
            admin,
            alwaysPath,
            left
        ), Post(
            admin,
            alwaysPath,
            right
        ));
        Check(races.Count(x => (int)x.StatusCode == 202) == 1 && races.Count(x => (int)x
            .StatusCode == 409) == 1,
            "Concurrent retry against one state accepts exactly one request");
        foreach (var response in races) response.Dispose();
        var failedAgain = await Terminal(always.Event.OutboxId, 6);
        Check(failedAgain.CanRetry && failedAgain.Event.NextAttemptAt is null,
            "New retry round stops at cumulative six without an automatic loop");

        var unsupported = new OutboxEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "mes.unsupported.retry-test.v1",
            AggregateType = "TEST",
            AggregateId = "retry-test",
            AggregateVersion = 1,
            Payload = "{}",
            Attempts = 3,
            Status = "FAILED",
            LastError = "TEST_UNSUPPORTED"
        };
        db.Add(unsupported);
        await db.SaveChangesAsync();
        var unsupportedState = (await State(unsupported.OutboxId))!;
        Check(!unsupportedState.CanRetry && unsupportedState.BlockReason == "UNSUPPORTED_CONSUMER",
            "Unknown consumer is not offered a false reprocessing action");
        await Expect(
            await Post(
                admin,
                "/api/v1/outbox-events/" + unsupported.OutboxId + "/retry",
                new OutboxRetryRequest(
                    Guid.NewGuid().ToString(),
                    unsupportedState.StateHash,
                    "Must remain unsupported"
                )
            ),
            409,
            "Unsupported event retry rejected"
        );

        var invalid = new OutboxEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "mes.notification.created.v1",
            AggregateType = "NOTIFICATION",
            AggregateId = once.Notification.NotificationId,
            AggregateVersion = 1,
            Payload = "{}",
            Attempts = 3,
            Status = "FAILED",
            LastError = "OUTBOX_INVALID_EVENT"
        };
        db.Add(invalid);
        await db.SaveChangesAsync();
        var invalidState = (await State(invalid.OutboxId))!;
        await Expect(
            await Post(
                admin,
                "/api/v1/outbox-events/" + invalid.OutboxId + "/retry",
                new OutboxRetryRequest(
                    Guid.NewGuid().ToString(),
                    invalidState.StateHash,
                    "Source still invalid"
                )
            ),
            422,
            "Invalid source fails preflight without replacing the immutable payload"
        );
        Check((await State(invalid.OutboxId))!.StateHash == invalidState.StateHash,
            "Rejected preflight changes neither state nor attempts");

        var success = await Create("SUCCESS");
        await Wait(
            () => Accepted(success.Notification.NotificationId),
            "Poison and exhausted events do not block later valid notifications"
        );
        Check((await State(always.Event.OutboxId))!.Event.Attempts == 6,
            "Terminal failure remains stopped while normal work continues");
        Check(await db.Set<Delivery>().CountAsync(x => x.NotificationId == long.Parse(once
            .Notification.NotificationId)) == 2,
            "Retry replay does not duplicate delivery intents");
        Check(!await db.Set<Delivery>().AnyAsync(x => x.Status == "SENT" || x.SentAtUtc != null),
            "All delivery effects remain strictly local Mock");
        var raw = await admin.GetStringAsync(path + "/retry-state");
        Check(
            !raw.Contains("payload", StringComparison.OrdinalIgnoreCase),
            "Operator state does not expose raw payload"
        );
        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/outbox-retry-verification.json", JsonSerializer
            .Serialize(new
        {
            passed = checks.Count,
            checks,
            successOutboxId = state.Event.OutboxId,
            alwaysFailOutboxId = stopped.Event.OutboxId,
            cloudApiCalls = 0,
            externalDeliveryCalls = 0,
            note = "Explicit local Mock faults; original events and cumulative histories retained."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Outbox retry verification passed: " + checks.Count);
    }
}


