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

public static class DeliveryVerification
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
        await Expect(
            await anonymous.GetAsync("/api/v1/deliveries"),
            401,
            "Anonymous delivery history denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/deliveries"),
            403,
            "Worker delivery history denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/outbox-events"),
            403,
            "Worker Outbox history denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/delivery-mock-statuses?deliveryIds=1"),
            403,
            "Worker Mock status denied"
        );
        var successRequest = new MockNotificationRequest(Guid.NewGuid().ToString(), "SUCCESS");
        await Expect(
            await Post(
                worker,
                "/api/v1/mock-notifications",
                successRequest
            ),
            403,
            "Worker Mock create denied"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/mock-notifications", successRequest),
            403,
            "Mock create requires CSRF"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/mock-notifications",
                successRequest with {
                    Scenario = "REAL_SMTP"
                }
            ),
            422,
            "Real provider scenario rejected"
        );
        await Expect(
            await jobs.PostAsJsonAsync("/internal/v1/jobs/outbox/dispatch-batch",
                new DispatchBatchRequest(0)),
            400,
            "Dispatch batch bound"
        );
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/jobs/outbox/dispatch-batch",
                new DispatchBatchRequest(1)),
            403,
            "Wrong service cannot dispatch Outbox"
        );
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/jobs/deliveries/dispatch-batch",
                new DispatchBatchRequest(1)),
            403,
            "Wrong service cannot dispatch delivery"
        );
        await Expect(
            await anonymous.PostAsJsonAsync("/internal/v1/jobs/outbox/dispatch-batch",
                new DispatchBatchRequest(1)),
            404,
            "Web does not expose internal dispatch"
        );
        async Task<MockNotificationResult> Create(string scenario) => await Read<MockNotificationResult>(
            await Post(
                admin,
                "/api/v1/mock-notifications",
                new MockNotificationRequest(Guid.NewGuid().ToString(), scenario)
            ),
            201,
            "Create " + scenario
        );
        async Task<DeliveryView[]> Rows(string id) =>
            (await admin
                .GetFromJsonAsync<PageResult<DeliveryView>>("/api/v1/deliveries?notificationId=" + id))!.Items;
        async Task<DeliveryView[]> AwaitRows(
            string id,
            Func<DeliveryView[], bool> ready,
            string label
        ) =>
            await Wait(async () => { var rows = await Rows(id); return ready(rows) ? rows : null; }, label);
        async Task<DeliveryMockStatus[]> Statuses(DeliveryView[] rows) =>
            (await admin
                .GetFromJsonAsync<DeliveryMockStatus[]>("/api/v1/delivery-mock-statuses?deliveryIds="
                + string.Join(',', rows.Select(x => x.DeliveryId))))!;
        async Task<DeliveryView[]> Accepted(string id, string label) => await Wait(async () =>
        {
            var rows = await Rows(id);
            if (rows.Length != 2) return null;
            return (await Statuses(rows)).All(x => x.Outcome == "ACCEPTED")
                ? await Rows(id) : null;
        }, label);
        var success = await Read<MockNotificationResult>(
            await Post(
                admin,
                "/api/v1/mock-notifications",
                successRequest
            ),
            201,
            "Create success notification"
        );
        var duplicate = await Read<MockNotificationResult>(
            await Post(
                admin,
                "/api/v1/mock-notifications",
                successRequest
            ),
            200,
            "Same Mock request replay"
        );
        Check(
            success.NotificationId == duplicate.NotificationId,
            "Mock request replay does not create notification"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/mock-notifications",
                successRequest with {
                    Scenario = "FAIL_ONCE"
                }
            ),
            409,
            "Same request with changed scenario rejected"
        );
        var successRows = await Accepted(success.NotificationId,
            "Quartz transfers Outbox and Mock accepts both channels");
        Check(successRows.Select(x => x.Channel).Order().SequenceEqual(new[] {
            "CHAT",
            "EMAIL"
        }), "EMAIL and CHAT intents");
        Check(successRows.All(x => x.Status == "PENDING" && x.SentAt == null && x.Attempts == 1),
            "Mock success never becomes real SENT");
        var firstReceipt = await admin
            .GetFromJsonAsync<MockReceiptView>("/api/v1/deliveries/" + successRows[0]
            .DeliveryId + "/mock-receipt");
        Check(firstReceipt!.Provider == "LOCAL_MOCK" && firstReceipt.Subject.Contains("SUCCESS")
            && firstReceipt.Body.Contains("외부 발송 없는") && firstReceipt.MessageHash.Length == 64,
            "Mock receives message content and durable hash");
        await Expect(
            await worker.GetAsync("/api/v1/deliveries/" + successRows[0]
                .DeliveryId + "/mock-receipt"),
            403,
            "Worker receipt content denied"
        );
        var id = long.Parse(success.NotificationId);
        Check(await db.Set<NotificationRecipient>().CountAsync(x => x.NotificationId == id) == 1,
            "Mock scenario only targets current administrator");
        var original = await db.Set<OutboxEvent>().AsNoTracking().FirstAsync(x =>
            x.AggregateType == "NOTIFICATION" && x.AggregateId == success.NotificationId);
        Check(original.Status == "DELIVERED", "Outbox durable responsibility transferred");
        var root = JsonNode.Parse(original.Payload)!;
        var replayId = Guid.NewGuid().ToString();
        root["eventId"] = replayId;
        var replay = new OutboxEvent
        {
            EventId = replayId,
            Type = original.Type,
            AggregateType = original.AggregateType,
            AggregateId = original.AggregateId,
            AggregateVersion = original.AggregateVersion,
            Payload = root.ToJsonString(),
            Status = "PENDING"
        };
        db.Add(replay);
        await db.SaveChangesAsync();
        await Wait(async () => await db.Set<OutboxEvent>().AsNoTracking().SingleOrDefaultAsync(
            x => x.OutboxId == replay.OutboxId && x.Status == "DELIVERED"),
                "Duplicate notification event accepted idempotently");
        Check(
            (await Rows(success.NotificationId)).Length == 2,
            "Duplicate Outbox does not duplicate intents"
        );
        var batches = await Task.WhenAll(
            jobs.PostAsJsonAsync("/internal/v1/jobs/deliveries/dispatch-batch",
                new DispatchBatchRequest(100)),
            jobs.PostAsJsonAsync("/internal/v1/jobs/deliveries/dispatch-batch",
                new DispatchBatchRequest(100)));
        foreach (var response in batches)
        {
            var result = await Read<DispatchBatchResult>(
                response,
                200,
                "Concurrent scheduled-compatible Mock dispatch"
            );
            Check(
                result.Delivered == 0 && result.Examined <= 100,
                "Dispatch result never counts real sends"
            );
        }
        var frozen = await Rows(success.NotificationId);
        Check(
            frozen.All(x => x.Attempts == 1),
            "Accepted Mock is excluded from automatic repeats"
        );
        Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "DELIVERY"
            && x.EntityId == successRows[0].DeliveryId && x
                .Action == "delivery.mock.accepted") == 1,
            "Exactly one durable Mock receipt");
        var manual = await Create("FAIL_ONCE");
        var failed = await AwaitRows(
            manual.NotificationId,
            rows => rows.Length == 2 && rows.All(x => x.Status == "FAILED"),
            "Manual-failure scenario persists FAILED"
        );
        var selected = failed[0];
        await Expect(
            await Post(
                worker,
                "/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, "retry")
            ),
            403,
            "Worker retry denied"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, " ")
            ),
            422,
            "Retry reason required"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, "retry")),
            403,
            "Retry requires CSRF"
        );
        var retried = await Read<DeliveryView>(
            await Post(
                admin,
                "/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, "  Local review complete  ")
            ),
            200,
            "Administrator queues retry"
        );
        Check(retried.Attempts == 1 && retried.Status == "PENDING" && retried.Version > selected
            .Version,
            "Retry retains attempts and advances version");
        await Expect(
            await Post(
                admin,
                "/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, "again")
            ),
            409,
            "Stale retry cannot queue twice"
        );
        var otherChannel = failed[1];
        var races = await Task.WhenAll(
            Post(
                admin,
                "/api/v1/deliveries/" + otherChannel.DeliveryId + "/retry",
                new ReasonCommand(otherChannel.Version, "A")
            ),
            Post(
                admin,
                "/api/v1/deliveries/" + otherChannel.DeliveryId + "/retry",
                new ReasonCommand(otherChannel.Version, "B")
            ));
        Check(races.Count(x => x.StatusCode == HttpStatusCode.OK) == 1
            && races.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
                "Concurrent manual retries accept exactly one version");
        foreach (var response in races) response.Dispose();
        var manualDone = await Accepted(manual.NotificationId,
            "Manual retry reaches Mock receipt on both channels");
        Check(manualDone.All(x => x.Attempts == 2 && x.Status == "PENDING" && x.SentAt == null),
            "Retried Mock success still has no real send");
        await Expect(
            await Post(
                admin,
                "/api/v1/deliveries/" + manualDone[0].DeliveryId + "/retry",
                new ReasonCommand(manualDone[0].Version, "repeat")
            ),
            409,
            "Accepted Mock cannot be manually resent"
        );
        var transient = await Create("TRANSIENT_ONCE");
        await AwaitRows(
            transient.NotificationId,
            rows => rows.Length == 2 && rows.All(x =>
                x.Status == "PENDING" && x.Attempts == 1 && x.NextAttemptAt != null && x
                    .LastError == "MOCK_TRANSIENT"),
            "Transient failure schedules automatic backoff"
        );
        var transientDone = await Accepted(transient.NotificationId,
            "Automatic retry reaches Mock receipt");
        Check(
            transientDone.All(x => x.Attempts == 2 && x.LastError == null),
            "Automatic retry keeps cumulative count"
        );
        var always = await Create("ALWAYS_FAIL");
        var exhausted = await AwaitRows(
            always.NotificationId,
            rows => rows.Length == 2 && rows.All(x => x.Status == "FAILED"),
            "Automatic retry budget exhausts"
        );
        Check(exhausted.All(
            x => x.Attempts == 3 && x.NextAttemptAt == null && x.LastError == "MOCK_RETRY_LIMIT"
        ),
            "Retry budget is three and does not loop forever");
        selected = exhausted[0];
        await Read<DeliveryView>(
            await Post(
                admin,
                "/api/v1/deliveries/" + selected.DeliveryId + "/retry",
                new ReasonCommand(selected.Version, "New retry round")
            ),
            200,
            "Administrator starts new bounded round"
        );
        await AwaitRows(
            always.NotificationId,
            rows => rows.SingleOrDefault(x => x.DeliveryId == selected.DeliveryId)
                is { Status: "FAILED", Attempts: 6 },
            "New retry round preserves total attempts and stops at six"
        );
        // Negative boundaries use isolated DB fixtures; no real transport exists.
        var inactiveLogin = "delivery." + Guid.NewGuid().ToString("N")[..10];
        var inactive = new UserAccount
        {
            Login = inactiveLogin,
            NormalizedLogin = inactiveLogin.ToUpperInvariant(),
            Name = "Inactive delivery fixture",
            Active = false,
            PasswordHash = "inactive-test-not-a-login",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        db.Add(inactive);
        await db.SaveChangesAsync();
        async Task<Delivery> SeedIntent(long recipient, string target)
        {
            var notification = new Notification {
                EventId = Guid.NewGuid().ToString(),
                Title = "Delivery boundary fixture",
                Body = "Local only"
            };
            db.Add(notification);
            await db.SaveChangesAsync();
            db.Add(new NotificationRecipient {
                NotificationId = notification.NotificationId,
                UserId = recipient
            });
            var row = new Delivery
            {
                NotificationId = notification.NotificationId,
                RecipientId = recipient,
                Channel = "EMAIL",
                DestinationKey = "mock.email.user." + recipient,
                TargetRef = target,
                Status = "PENDING"
            };
            db.Add(row);
            await db.SaveChangesAsync();
            return row;
        }
        var inactiveIntent = await SeedIntent(inactive.UserId, "mock://local/email/" + inactive
            .UserId + "/SUCCESS");
        var rejectedIntent = await SeedIntent(long.Parse(me.UserId),
            "https://example.invalid/private-hook");
        await Wait(async () => await db.Set<Delivery>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeliveryId == inactiveIntent.DeliveryId && x.LastError == "RECIPIENT_INACTIVE"),
                "Inactive recipient blocked at dispatch");
        await Wait(async () => await db.Set<Delivery>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeliveryId == rejectedIntent.DeliveryId && x.LastError == "MOCK_TARGET_REJECTED"),
                "Real URL rejected without transport");
        var poisonId = Guid.NewGuid().ToString();
        var poison = new OutboxEvent
        {
            EventId = poisonId,
            Type = "mes.notification.created.v1",
            AggregateType = "NOTIFICATION",
            AggregateId = Id(rejectedIntent.NotificationId),
            AggregateVersion = 1,
            Status = "PENDING",
            Payload = CollectionFormat.Canonical(new
            {
                eventId = poisonId,
                type = "mes.notification.created.v1",
                schemaVersion = 1,
                data = new {
                    notificationId = Id(rejectedIntent.NotificationId),
                    title = "Delivery boundary fixture",
                    body = "Local only",
                    recipientIds = new[] {
                        me.UserId,
                        long.MaxValue.ToString()
                    }
                }
            })
        };
        db.Add(poison);
        var unknown = new OutboxEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "mes.unsupported.future.v1",
            AggregateType = "FUTURE",
            AggregateId = "future-test",
            AggregateVersion = 1,
            Payload = "{}",
            Status = "PENDING"
        };
        db.Add(unknown);
        await db.SaveChangesAsync();
        await Wait(async () => await db.Set<OutboxEvent>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OutboxId == poison.OutboxId
            && x.Status == "FAILED" && x.NextAttemptAtUtc == null),
                "Poison Outbox is isolated after retry budget");
        Check(await db.Set<Delivery>().CountAsync(x => x.NotificationId == rejectedIntent
            .NotificationId) == 1,
            "Invalid recipient snapshot creates no partial extra intent");
        var unknownSaved = await db.Set<OutboxEvent>().AsNoTracking().SingleAsync(x => x
            .OutboxId == unknown.OutboxId);
        Check(
            unknownSaved.Status == "PENDING" && unknownSaved.Attempts == 0,
            "Unsupported type is not falsely consumed"
        );
        var later = await Create("SUCCESS");
        await Accepted(later.NotificationId,
            "Poison event does not block later valid notification");
        await Expect(
            await admin.GetAsync("/api/v1/deliveries?limit=101"),
            400,
            "Delivery pagination bound"
        );
        await Expect(
            await admin.GetAsync("/api/v1/deliveries?channel=SMTP"),
            422,
            "Channel filter validation"
        );
        await Expect(
            await admin.GetAsync("/api/v1/outbox-events?status=SENT"),
            422,
            "Outbox status validation"
        );
        var raw = await admin.GetStringAsync("/api/v1/deliveries?notificationId=" + success
            .NotificationId);
        Check(!raw.Contains("targetRef", StringComparison.OrdinalIgnoreCase) && !raw
            .Contains("example.invalid"),
            "Public delivery DTO hides target refs and URLs");
        var rawOutbox = await admin.GetStringAsync("/api/v1/outbox-events");
        Check(
            !rawOutbox.Contains("payload", StringComparison.OrdinalIgnoreCase),
            "Outbox admin view hides raw payload"
        );
        Check(!await db.Set<Delivery>().AnyAsync(x => x.Status == "SENT" || x.SentAtUtc != null),
            "All local deliveries remain real-send count zero");
        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/deliveries-verification.json", JsonSerializer
            .Serialize(new
        {
            passed = checks.Count,
            checks,
            successNotificationId = success.NotificationId,
            manualNotificationId = manual.NotificationId,
            cloudApiCalls = 0,
            externalDeliveryCalls = 0,
            note = "Local Mock receipts are distinct from SENT; isolated histories retained."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Delivery verification passed: " + checks.Count);
    }

    private static string Id(long id) => id.ToString(System.Globalization.CultureInfo
        .InvariantCulture);
}



