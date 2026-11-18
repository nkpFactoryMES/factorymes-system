using System.Globalization;
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

// 고유 관리자 알림을 Inbox/Outbox/Delivery에 접수하고 실제 로컬 SMTP·HTTP 수신과 대조한다.
// Mailpit 메일이나 다른 사용자의 이력을 삭제하지 않으며 외부 SMTP/채팅 서비스는 호출하지 않는다.
public static class LocalDeliveryVerification
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

        using var pinned = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client(string address, bool cookies = true) => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = cookies,
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (
                _,
                cert,
                _,
                _
            ) =>
                cert?.Thumbprint == pinned.Thumbprint
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var anonymous = Client("https://web:8443");
        using var jobs = Client("http://mes-backend:8080");
        using var wrong = Client("http://mes-backend:8080");
        using var mailpit = Client("http://mailpit:8025", false);
        using var capture = Client("http://chat-receiver:8080", false);

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
                Check((int)response.StatusCode == status, label + " HTTP " + (int)response.StatusCode);
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
                Check((int)response.StatusCode == status, label + " HTTP " + (int)response.StatusCode);
            }
        }

        async Task<T> Wait<T>(Func<Task<T?>> read, string label) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(100);
            while (DateTime.UtcNow < until)
            {
                var result = await read();
                if (result is not null)
                {
                    Check(true, label);
                    return result;
                }
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
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token.GetProperty("accessToken").GetString()
            );
        }

        var administrator = await Read<SessionUser>(
            await Post(
                admin,
                "/api/v1/auth/login",
                new
                {
                    login = "admin",
                    password = secret("admin_password")
                }
            ),
            200,
            "Administrator Cookie login"
        );
        await Read<SessionUser>(
            await Post(
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
        await Authenticate(jobs, "jobs-worker");
        await Authenticate(wrong, "verification");

        var request = new LocalNotificationRequest(Guid.NewGuid().ToString(), "SUCCESS");
        await Expect(
            await anonymous.GetAsync("/api/v1/delivery-local-statuses?deliveryIds=1"),
            401,
            "Anonymous cannot read local receipts"
        );
        await Expect(
            await worker.GetAsync("/api/v1/delivery-local-statuses?deliveryIds=1"),
            403,
            "Worker cannot read local receipt status"
        );
        await Expect(
            await Post(
                worker,
                "/api/v1/local-notifications",
                request
            ),
            403,
            "Worker cannot create local adapter notification"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/local-notifications", request),
            403,
            "Local adapter create requires CSRF"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/local-notifications",
                request with
                {
                    Scenario = "EXTERNAL_SMTP"
                }
            ),
            422,
            "Unapproved provider scenario rejected"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/local-notifications",
                new
                {
                    clientRequestId = Guid.NewGuid().ToString(),
                    scenario = "SUCCESS",
                    endpoint = "https://example.invalid/hook"
                }
            ),
            400,
            "Arbitrary endpoint field rejected before dispatch"
        );
        await Expect(
            await admin.GetAsync("/api/v1/delivery-local-statuses?deliveryIds="),
            400,
            "Empty status identifier list rejected"
        );
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/jobs/deliveries/dispatch-batch",
                new DispatchBatchRequest(1)),
            403,
            "Wrong service cannot execute delivery adapter"
        );

        async Task<DeliveryView[]> Rows(string notification) =>
            (await admin.GetFromJsonAsync<PageResult<DeliveryView>>(
                "/api/v1/deliveries?notificationId=" + notification))!.Items;

        async Task<LocalDeliveryStatus[]> Statuses(DeliveryView[] rows) =>
            (await admin.GetFromJsonAsync<LocalDeliveryStatus[]>(
                "/api/v1/delivery-local-statuses?deliveryIds="
                + string.Join(',', rows.Select(x => x.DeliveryId))))!;

        async Task<DeliveryView[]> Accepted(string notification, string label) =>
            await Wait(async () =>
            {
                var rows = await Rows(notification);
                if (rows.Length != 2)
                {
                    return null;
                }
                var statuses = await Statuses(rows);
                return statuses.Length == 2 && statuses.All(x => x.Outcome == "ACCEPTED")
                    ? await Rows(notification) : null;
            }, label);

        async Task<MockNotificationResult> Create(string scenario) =>
            await Read<MockNotificationResult>(
                await Post(
                    admin,
                    "/api/v1/local-notifications",
                    new LocalNotificationRequest(Guid.NewGuid().ToString(), scenario)
                ),
                201,
                "Create local " + scenario
            );

        var created = await Read<MockNotificationResult>(
            await Post(
                admin,
                "/api/v1/local-notifications",
                request
            ),
            201,
            "Create actual local capture notification"
        );
        var replayed = await Read<MockNotificationResult>(
            await Post(
                admin,
                "/api/v1/local-notifications",
                request
            ),
            200,
            "Replay identical local notification request"
        );
        Check(created.NotificationId == replayed.NotificationId,
            "Same request retains exactly one notification");
        await Expect(
            await Post(
                admin,
                "/api/v1/local-notifications",
                request with
                {
                    Scenario = "CHAT_ALWAYS_FAIL"
                }
            ),
            409,
            "Same request with changed scenario conflicts"
        );
        var accepted = await Accepted(created.NotificationId,
            "Quartz transfers common Outbox and actual SMTP HTTP capture both accept");
        Check(accepted.Select(x => x.Channel).Order().SequenceEqual(new[] {
            "CHAT",
            "EMAIL"
        }),
            "Two durable channel intents are created");
        Check(accepted.All(x => x.Status == "PENDING" && x.SentAt is null && x.Attempts == 1),
            "Local capture acceptance does not claim external SENT");
        Check(accepted.All(x => x.DestinationKey.StartsWith("local.", StringComparison.Ordinal)),
            "Local notification routes to local provider namespace only");
        var notificationId = long.Parse(created.NotificationId, CultureInfo.InvariantCulture);
        var notification = await db.Set<Notification>().AsNoTracking().SingleAsync(x =>
            x.NotificationId == notificationId);
        Check(await db.Set<NotificationRecipient>().CountAsync(x =>
            x.NotificationId == notificationId) == 1,
            "Local scenario uses current administrator inbox recipient only");
        Check(await db.Set<NotificationRecipient>().AnyAsync(x =>
            x.NotificationId == notificationId
            && x.UserId == long.Parse(administrator.UserId, CultureInfo.InvariantCulture)),
            "Local inbox and delivery recipient is the authenticated administrator");
        var outbox = await db.Set<OutboxEvent>().AsNoTracking().FirstAsync(x =>
            x.AggregateType == "NOTIFICATION" && x.AggregateId == created.NotificationId);
        Check(outbox.Status == "DELIVERED", "Common Outbox responsibility is durably transferred");
        var smtp = accepted.Single(x => x.Channel == "EMAIL");
        var chat = accepted.Single(x => x.Channel == "CHAT");

        async Task<LocalReceiptView> Receipt(DeliveryView row) =>
            (await admin.GetFromJsonAsync<LocalReceiptView>(
                "/api/v1/deliveries/" + row.DeliveryId + "/local-receipt"))!;

        var smtpReceipt = await Receipt(smtp);
        var chatReceipt = await Receipt(chat);
        Check(smtpReceipt.Provider == "LOCAL_SMTP" && chatReceipt.Provider == "LOCAL_WEBHOOK",
            "Receipt distinguishes actual SMTP and local webhook from Mock");
        foreach (var row in accepted)
        {
            var receipt = await Receipt(row);
            var expectedHash = CollectionFormat.Hash(new
            {
                deliveryId = row.DeliveryId,
                row.Channel,
                row.DestinationKey,
                subject = notification.Title,
                body = notification.Body
            });
            Check(receipt.MessageHash == expectedHash && receipt.Subject == notification.Title
                && receipt.Body == notification.Body,
                row.Channel + " receipt binds actual content and destination hash");
            Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "DELIVERY"
                && x.EntityId == row.DeliveryId && x.Action == "delivery.local.accepted") == 1,
                row.Channel + " has one durable accepted receipt");
        }
        await Expect(
            await worker.GetAsync("/api/v1/deliveries/" + smtp.DeliveryId
                + "/local-receipt"),
            403,
            "Worker cannot inspect SMTP receipt content"
        );
        await Expect(
            await admin.GetAsync("/api/v1/deliveries/" + smtp.DeliveryId
                + "/mock-receipt"),
            404,
            "Local transport is not reported as Mock receipt"
        );

        // 공식 Mailpit API v1은 messages[].MessageID와 /message/{ID}의 decoded Text를 제공한다.
        // 최신 메일 한 건을 임의 선택하지 않고 이 발송의 안정적 Message-ID로 찾는다.
        async Task<JsonElement[]> MailMatches(string deliveryId)
        {
            var messages = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/messages?limit=500");
            return messages.GetProperty("messages").EnumerateArray().Where(x =>
                x.GetProperty("MessageID").GetString()?.Trim('<', '>')
                == "delivery-" + deliveryId + "@factorymes.invalid").Select(x => x.Clone()).ToArray();
        }

        var mailMatches = await MailMatches(smtp.DeliveryId);
        Check(mailMatches.Length == 1, "Mailpit has one actual SMTP Message-ID for this delivery");
        var mailId = mailMatches[0].GetProperty("ID").GetString()!;
        var mail = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/message/" + mailId);
        static string NormalizedText(string value) => value.Replace("\r\n", "\n").TrimEnd('\n');
        Check(mail.GetProperty("Subject").GetString() == notification.Title
            && NormalizedText(mail.GetProperty("Text").GetString()!) == NormalizedText(notification.Body),
            "Mailpit decodes UTF8 SMTP subject and body without substitution");
        var raw = await mailpit.GetStringAsync("/api/v1/message/" + mailId + "/raw");
        Check(raw.Contains("X-FactoryMES-Delivery-Id: " + smtp.DeliveryId, StringComparison.Ordinal),
            "SMTP carries stable delivery identifier header");
        Check(mail.GetProperty("To").EnumerateArray().All(x =>
            x.GetProperty("Address").GetString()!.EndsWith("@factorymes.invalid", StringComparison.Ordinal)),
            "SMTP capture uses reserved invalid-domain recipients only");

        var captured = await capture.GetFromJsonAsync<JsonElement>("/captures/" + chat.DeliveryId);
        var expectedText = ("[FactoryMES 로컬 검증] " + notification.Title + "\n" + notification.Body)
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        Check(captured.GetProperty("deliveryId").GetString() == chat.DeliveryId
            && captured.GetProperty("messageHash").GetString() == chatReceipt.MessageHash
            && captured.GetProperty("text").GetString() == expectedText,
            "Independent local receiver captured the real UTF8 webhook payload and binding hash");

        async Task<HttpResponseMessage> SendCapture(string text, string hash)
        {
            using var replay = new HttpRequestMessage(HttpMethod.Post, "/hooks/factorymes")
            {
                Content = JsonContent.Create(new
                {
                    text,
                    mrkdwn = false,
                    unfurl_links = false,
                    unfurl_media = false
                })
            };
            replay.Headers.Add("X-FactoryMES-Delivery-Id", chat.DeliveryId);
            replay.Headers.Add("X-FactoryMES-Message-Hash", hash);
            replay.Headers.Add("X-FactoryMES-Scenario", "SUCCESS");
            return await capture.SendAsync(replay);
        }

        await Expect(
            await SendCapture(expectedText, chatReceipt.MessageHash),
            200,
            "Identical webhook replay is accepted idempotently by local receiver"
        );
        var recaptured = await capture.GetFromJsonAsync<JsonElement>("/captures/" + chat.DeliveryId);
        Check(recaptured.GetProperty("acceptedAt").GetDateTime()
            == captured.GetProperty("acceptedAt").GetDateTime(),
            "Local webhook replay retains original captured acceptance identity");
        await Expect(
            await SendCapture(expectedText + " changed", chatReceipt.MessageHash),
            409,
            "Same webhook delivery id with changed body conflicts"
        );

        var replayPayload = JsonNode.Parse(outbox.Payload)!;
        var eventId = Guid.NewGuid().ToString();
        replayPayload["eventId"] = eventId;
        var eventReplay = new OutboxEvent
        {
            EventId = eventId,
            Type = outbox.Type,
            AggregateType = outbox.AggregateType,
            AggregateId = outbox.AggregateId,
            AggregateVersion = outbox.AggregateVersion,
            Payload = replayPayload.ToJsonString(),
            Status = "PENDING"
        };
        db.Add(eventReplay);
        await db.SaveChangesAsync();
        await Wait(async () => await db.Set<OutboxEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.OutboxId == eventReplay.OutboxId && x.Status == "DELIVERED"),
            "Replayed Outbox event completes without duplicate channel intents");
        await Read<DispatchBatchResult>(
            await jobs.PostAsJsonAsync(
                "/internal/v1/jobs/deliveries/dispatch-batch", new DispatchBatchRequest(100)),
            200,
            "Repeat dispatch excludes accepted local deliveries"
        );
        Check((await Rows(created.NotificationId)).All(x => x.Attempts == 1),
            "Accepted SMTP and webhook are not sent again by normal dispatch");
        Check((await MailMatches(smtp.DeliveryId)).Length == 1,
            "Repeat Outbox and dispatch do not create another SMTP message");

        var transient = await Create("CHAT_TRANSIENT_ONCE");
        var recovered = await Accepted(transient.NotificationId,
            "Real HTTP429 automatic retry reaches captured webhook");
        var recoveredChat = recovered.Single(x => x.Channel == "CHAT");
        Check(recoveredChat.Attempts == 2 && recoveredChat.LastError is null,
            "HTTP429 retry preserves cumulative attempts and clears transient failure");
        Check(recovered.Single(x => x.Channel == "EMAIL").Attempts == 1,
            "Chat backoff does not resend already accepted SMTP channel");
        var retriedCapture = await capture.GetFromJsonAsync<JsonElement>(
            "/captures/" + recoveredChat.DeliveryId);
        Check(retriedCapture.GetProperty("attempts").GetInt32() == 2,
            "Independent receiver observed failed and successful webhook attempts");

        var permanent = await Create("CHAT_PERMANENT_FAIL");
        var permanentlyFailed = await Wait(async () =>
        {
            var rows = await Rows(permanent.NotificationId);
            return rows.Length == 2 && rows.Single(x => x.Channel == "CHAT").Status == "FAILED"
                ? rows : null;
        }, "Real HTTP400 becomes permanent failure without automatic retry");
        var permanentChat = permanentlyFailed.Single(x => x.Channel == "CHAT");
        Check(permanentChat.Attempts == 1 && permanentChat.NextAttemptAt is null,
            "Permanent HTTP error has no automatic retry schedule");
        await Expect(
            await admin.GetAsync("/api/v1/deliveries/" + permanentChat.DeliveryId
                + "/local-receipt"),
            404,
            "Rejected webhook has no fabricated accepted receipt"
        );

        var always = await Create("CHAT_ALWAYS_FAIL");
        var exhausted = await Wait(async () =>
        {
            var rows = await Rows(always.NotificationId);
            return rows.Length == 2 && rows.Single(x => x.Channel == "CHAT").Status == "FAILED"
                ? rows : null;
        }, "Real HTTP503 stops after bounded automatic retry budget");
        var failed = exhausted.Single(x => x.Channel == "CHAT");
        Check(failed.Attempts == 3 && failed.NextAttemptAt is null,
            "Temporary HTTP failure is bounded to three attempts");
        await Expect(
            await Post(
                worker,
                "/api/v1/deliveries/" + failed.DeliveryId + "/retry",
                new ReasonCommand(failed.Version, "retry")
            ),
            403,
            "Worker cannot retry local transport"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/deliveries/" + failed.DeliveryId
                + "/retry", new ReasonCommand(failed.Version, "retry")),
            403,
            "Local manual retry requires CSRF"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/deliveries/" + failed.DeliveryId + "/retry",
                new ReasonCommand(failed.Version, " ")
            ),
            422,
            "Local retry requires reason"
        );
        await Read<DeliveryView>(
            await Post(
                admin,
                "/api/v1/deliveries/" + failed.DeliveryId + "/retry",
                new ReasonCommand(failed.Version, "Local capture retry verification")
            ),
            200,
            "Administrator starts a new bounded local retry round"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/deliveries/" + failed.DeliveryId + "/retry",
                new ReasonCommand(failed.Version, "stale replay")
            ),
            409,
            "Stale version cannot queue local retry twice"
        );
        var manualFailed = await Wait(async () =>
        {
            var rows = await Rows(always.NotificationId);
            return rows.SingleOrDefault(x => x.DeliveryId == failed.DeliveryId)
                is { Status: "FAILED", Attempts: 6 } ? rows : null;
        }, "Manual retry retains delivery id and exhausts one additional bounded round");
        Check(manualFailed.Single(x => x.Channel == "EMAIL").Attempts == 1,
            "Manual chat retry does not affect SMTP acceptance");
        Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "DELIVERY"
            && x.EntityId == failed.DeliveryId && x.Action == "delivery.local.accepted") == 0,
            "Exhausted webhook never receives an accepted audit or Mock fallback");
        Check((await Rows(created.NotificationId)).All(x => x.SentAt is null),
            "Final local success still has no external send timestamp");

        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/delivery-adapters-verification.json",
            JsonSerializer.Serialize(new
            {
                passed = checks.Count,
                checks,
                successNotificationId = created.NotificationId,
                smtpDeliveryId = smtp.DeliveryId,
                chatDeliveryId = chat.DeliveryId,
                mailpitMessageId = mailId,
                transientNotificationId = transient.NotificationId,
                permanentNotificationId = permanent.NotificationId,
                permanentChatDeliveryId = permanentChat.DeliveryId,
                exhaustedNotificationId = always.NotificationId,
                exhaustedChatDeliveryId = failed.DeliveryId,
                actualSmtpCapture = true,
                actualHttpCapture = true,
                externalDeliveries = 0,
                paidApiCalls = 0,
                note = "Local SMTP/HTTP integration only; no external provider compatibility certification. "
                    + "SMTP Message-ID does not guarantee receiver-side deduplication after ambiguous ACK."
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        Console.WriteLine("Local delivery adapter verification passed: " + checks.Count);
    }
}






