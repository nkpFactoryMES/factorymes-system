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

public static class AlarmVerification
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
        var key = "A" + Guid.NewGuid().ToString("N")[..12];
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
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var stranger = Client("https://web:8443");
        using var jobs = Client("http://mes-backend:8080");
        using var wrong = Client("http://mes-backend:8080");
        using var anonymous = Client("http://mes-backend:8080");
        async Task<HttpResponseMessage> Public(
            HttpClient client,
            string route,
            object body,
            bool put = false
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken")
                .GetString());
            return await client.SendAsync(new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod
                .Post, route)
            {
                Content = JsonContent.Create(body, body.GetType())
            });
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
        async Task Login(
            HttpClient client,
            string login,
            string passwordFile
        ) =>
            await Read<JsonElement>(
                await Public(
                    client,
                    "/api/v1/auth/login",
                    new {
                        login,
                        password = secret(passwordFile)
                    }
                ),
                200,
                login + " login"
            );
        async Task Authenticate(HttpClient client, string identity)
        {
            using var response = await anonymous.PostAsJsonAsync("/internal/v1/auth/token", new
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
        async Task<T> Wait<T>(Func<Task<T?>> read, string label) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < until)
            {
                var value = await read();
                if (value is not null) { Check(true, label); return value; }
                await Task.Delay(300);
            }
            throw new InvalidOperationException("Timed out: " + label);
        }
        var line = new Line {
            Code = key,
            Name = "COMM_LOST test"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new Equipment {
            LineId = line.LineId,
            Code = key,
            Name = "COMM_LOST test " + key
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        TargetView? target = null;
        RuleView? rule = null;
        OrderView? order = null;
        try
        {
            await Login(
                admin,
                "admin",
                "admin_password"
            );
            await Login(
                worker,
                "portal",
                "portal_password"
            );
            await Login(
                stranger,
                "worker.demo",
                "worker_password"
            );
            await Authenticate(jobs, "jobs-worker");
            await Authenticate(wrong, "verification");
            await Expect(
                await anonymous.PostAsJsonAsync("/internal/v1/jobs/connections/watch",
                    new DispatchBatchRequest(100)),
                401,
                "Watch requires service authentication"
            );
            await Expect(
                await wrong.PostAsJsonAsync("/internal/v1/jobs/connections/watch",
                    new DispatchBatchRequest(100)),
                403,
                "Diagnostics identity cannot watch"
            );
            await Expect(
                await jobs.PostAsJsonAsync("/internal/v1/jobs/connections/watch",
                    new DispatchBatchRequest(0)),
                400,
                "Watch bound enforced"
            );
            await Expect(
                await admin.PostAsJsonAsync("/internal/v1/jobs/connections/watch",
                    new DispatchBatchRequest(100)),
                404,
                "Web does not expose internal watch"
            );
            var me = await worker.GetFromJsonAsync<SessionUser>("/api/v1/auth/me");
            var product = await db.Set<Product>().FirstAsync(x => x.Active == EF.Parameter(true));
            order = await Read<OrderView>(
                await Public(
                    admin,
                    "/api/v1/orders",
                    new OrderCreate(
                        product.ProductId.ToString(),
                        equipment.EquipmentId.ToString(),
                        me!.UserId,
                        "COMM_LOST test",
                        10,
                        "MANUAL",
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow.AddHours(1),
                        []
                    )
                ),
                201,
                "Create assigned work"
            );
            order = await Read<OrderView>(
                await Public(
                    admin,
                    "/api/v1/orders/" + order.OrderId + "/release",
                    new Command(order.Version)
                ),
                200,
                "Release assigned work"
            );
            var settings = new EquipmentSettings(
                "HTTP",
                "http://equipment-simulator:8080/equipment/" + key,
                60000,
                1000,
                2,
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
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    new EquipmentTargetCreate(
                        key,
                        "COMM_LOST test",
                        "equipment-http",
                        true,
                        "EQUIPMENT",
                        equipment.EquipmentId.ToString(),
                        settings
                    )
                ),
                201,
                "Register long-poll fixture"
            );
            var write = new RuleWrite(
                equipment.EquipmentId.ToString(),
                key,
                null,
                "COMM_LOST",
                "WARN",
                null,
                null,
                null,
                null,
                true
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/alarm-rules",
                    write
                ),
                403,
                "Worker cannot create rule"
            );
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/alarm-rules", write),
                403,
                "Rule requires CSRF"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        Kind = "HIGH"
                    }
                ),
                422,
                "Incomplete HIGH rejected"
            );
            rule = await Read<RuleView>(
                await Public(
                    admin,
                    "/api/v1/alarm-rules",
                    write
                ),
                201,
                "Create rule"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/alarm-rules",
                    write
                ),
                409,
                "One COMM_LOST rule per target"
            );
            var update = new RuleUpdate(
                rule.Version,
                write.EquipmentId,
                key,
                null,
                "COMM_LOST",
                "CRITICAL",
                null,
                null,
                null,
                null,
                true
            );
            rule = await Read<RuleView>(
                await Public(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    update,
                    true
                ),
                200,
                "Edit rule severity"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    update,
                    true
                ),
                409,
                "Stale rule version rejected"
            );
            await Expect(
                await worker.GetAsync("/api/v1/alarm-rules/" + rule.RuleId),
                403,
                "Worker rule detail denied"
            );
            var ownRules = await worker.GetFromJsonAsync<PageResult<RuleView>>(
                "/api/v1/alarm-rules?equipmentId=" + equipment.EquipmentId);
            Check(ownRules!.Items.Any(x => x.RuleId == rule.RuleId), "Assigned worker rule scope");
            var otherRules = await stranger.GetFromJsonAsync<PageResult<RuleView>>(
                "/api/v1/alarm-rules?equipmentId=" + equipment.EquipmentId);
            Check(otherRules!.Items.Length == 0, "Unassigned worker has no equipment rules");
            var id = await db.Set<IntegrationTarget>().Where(x => x.TargetCode == key)
                .Select(x => x.TargetId).SingleAsync();
            await Wait(async () => await db.Set<EquipmentConnection>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TargetId == id && x.LastValidSampleAtUtc != null),
                    "Actual first sample received");
            var active = await Wait(async () => await db.Set<Alarm>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.RuleId == long.Parse(rule.RuleId) && x
                    .Status == "ACTIVE"),
                "Quartz automatically opens COMM_LOST");
            Check(active.Kind == "COMM_LOST" && active.Severity == "CRITICAL" && active
                .RuleVersion == rule.Version,
                "Opening rule snapshot/version retained");
            var alarmId = active.AlarmId.ToString();
            await Expect(
                await stranger.GetAsync("/api/v1/alarms/" + alarmId),
                404,
                "Unassigned worker alarm hidden"
            );
            await Expect(
                await Public(
                    stranger,
                    "/api/v1/alarms/" + alarmId + "/ack",
                    new Command(active.Version)
                ),
                404,
                "Unassigned worker ACK hidden"
            );
            var workerAlarm = await worker.GetFromJsonAsync<AlarmView>("/api/v1/alarms/" + alarmId);
            Check(workerAlarm!.AlarmId == alarmId, "Assigned worker reads alarm");
            var notice = await db.Set<Notification>().AsNoTracking().SingleAsync(x => x
                .AlarmId == active.AlarmId);
            var recipientIds = await db.Set<NotificationRecipient>().Where(x => x
                .NotificationId == notice.NotificationId)
                .Select(x => x.UserId).ToArrayAsync();
            Check(recipientIds.Contains(long.Parse(me.UserId)) && recipientIds.Distinct()
                .Count() == recipientIds.Length,
                "Recipient snapshot includes assigned worker once");
            var strangerInbox = await stranger
                .GetFromJsonAsync<PageResult<NotificationView>>("/api/v1/inbox");
            Check(
                strangerInbox!.Items.All(x => x.NotificationId != notice.NotificationId.ToString()),
                "Inbox isolation"
            );
            await Expect(
                await Public(
                    stranger,
                    "/api/v1/inbox/" + notice.NotificationId + "/read",
                    new { },
                    true
                ),
                404,
                "Unowned notification read hidden"
            );
            await Read<AckResponse>(
                await Public(
                    worker,
                    "/api/v1/inbox/" + notice.NotificationId + "/read",
                    new { },
                    true
                ),
                200,
                "Read own notification"
            );
            var firstRead = await db.Set<NotificationRecipient>().AsNoTracking().SingleAsync(
                x => x.NotificationId == notice.NotificationId && x.UserId == long.Parse(me
                    .UserId));
            await Read<AckResponse>(
                await Public(
                    worker,
                    "/api/v1/inbox/" + notice.NotificationId + "/read",
                    new { },
                    true
                ),
                200,
                "Read replay accepted"
            );
            var secondRead = await db.Set<NotificationRecipient>().AsNoTracking().SingleAsync(
                x => x.NotificationId == notice.NotificationId && x.UserId == long.Parse(me
                    .UserId));
            Check(
                firstRead.ReadAtUtc == secondRead.ReadAtUtc && firstRead.ReadAtUtc != null,
                "First read timestamp immutable"
            );
            var beforeAck = await admin.GetFromJsonAsync<AlarmView>("/api/v1/alarms/" + alarmId);
            Check(beforeAck!.AckAt is null, "Reading does not ACK alarm");
            var ack = await Read<AlarmView>(
                await Public(
                    worker,
                    "/api/v1/alarms/" + alarmId + "/ack",
                    new Command(beforeAck.Version)
                ),
                200,
                "Worker ACK"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/alarms/" + alarmId + "/ack",
                    new Command(beforeAck.Version)
                ),
                409,
                "Stale ACK rejected"
            );
            var ackAgain = await Read<AlarmView>(
                await Public(
                    admin,
                    "/api/v1/alarms/" + alarmId + "/ack",
                    new Command(ack.Version)
                ),
                200,
                "Current ACK replay read-only"
            );
            Check(ackAgain.AckBy == me.UserId && ackAgain.AckAt == ack.AckAt && ackAgain
                .Version == ack.Version,
                "First ACK actor/time/version preserved");
            await Read<ConnectionWatchResult>(
                await jobs.PostAsJsonAsync("/internal/v1/jobs/connections/watch",
                    new DispatchBatchRequest(100)),
                200,
                "Authorized watch"
            );
            Check(await db.Set<Alarm>().CountAsync(x => x.RuleId == active.RuleId && x
                .Status == "ACTIVE") == 1
                && await db.Set<Notification>().CountAsync(x => x.AlarmId == active.AlarmId) == 1,
                "Repeated watch cannot duplicate episode/inbox");
            target = await admin.GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + key);
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets/" + key + "/configuration",
                    new TargetConfigUpdate(
                        target!.Version,
                        target.ConfigVersion,
                        target.DisplayName,
                        true,
                        "Restore regular collection",
                        settings with {
                            PollIntervalMs = 1000,
                            StaleSec = 20
                        }
                    ),
                    true
                ),
                200,
                "Resume frequent collection"
            );
            var recovered = await Wait(async () => await db.Set<Alarm>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.AlarmId == active.AlarmId && x.Status == "RECOVERED"),
                "Actual sample plus Quartz recovers alarm");
            Check(recovered.AckBy == long.Parse(me.UserId) && recovered.ClosedAtUtc is null
                && recovered.RuleSnapshot == active.RuleSnapshot && recovered
                    .RuleVersion == active.RuleVersion,
                "Recovery retains ACK/snapshot and does not close");
            Check(await db.Set<Notification>().CountAsync(x => x.AlarmId == active.AlarmId) == 2,
                "One recovery notification");
            var events = await db.Set<OutboxEvent>().AsNoTracking()
                .Where(x => x.AggregateType == "ALARM" && x.AggregateId == alarmId).ToArrayAsync();
            var changes = events.Select(x => JsonDocument.Parse(x.Payload).RootElement
                .GetProperty("data")
                .GetProperty("change").GetString()).ToArray();
            Check(changes.Order().SequenceEqual(new[] {
                "ACKNOWLEDGED",
                "OPENED",
                "RECOVERED"
            }.Order()),
                "Alarm events persisted once per transition");
            Check(events.All(x => x.Status is "PENDING" or "DELIVERED") && await db.Set<OutboxEvent>()
                .AnyAsync(x => x.Type == "mes.notification.created.v1"),
                    "Local Outbox retained without external delivery");
            await Expect(
                await worker.GetAsync("/api/v1/inbox?limit=101"),
                400,
                "Inbox pagination bound"
            );
            await Expect(
                await worker.GetAsync("/api/v1/alarms?status=INVALID"),
                422,
                "Alarm filter validation"
            );
            var unread = await worker
                .GetFromJsonAsync<PageResult<NotificationView>>("/api/v1/inbox?unreadOnly=true");
            Check(unread!.Items.All(x => x.ReadAt is null), "Unread filter per recipient");
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/FactoryMES-alarms-api-verification.json",
                JsonSerializer.Serialize(new
            {
                passed = true,
                count = checks.Count,
                checks,
                targetId = key,
                equipmentId = equipment
                    .EquipmentId.ToString(),
                alarmId,
                ruleId = rule.RuleId,
                orderId = order.OrderId,
                executedAt = DateTime.UtcNow
            },
                    new JsonSerializerOptions {
                        WriteIndented = true
                    }));
            Console.WriteLine("Alarm verification passed: " + checks.Count);
        }
        finally
        {
            if (target is not null)
            {
                var current = await admin
                    .GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + key);
                using var stop = await Public(
                    admin,
                    "/api/v1/integration-targets/" + key + "/state",
                    new TargetStateUpdate(
                        current!.Version,
                        current.ConfigVersion,
                        false,
                        "Verification complete"
                    ),
                    true
                );
                stop.EnsureSuccessStatusCode();
            }
            if (rule is not null)
            {
                var current = await admin.GetFromJsonAsync<RuleView>("/api/v1/alarm-rules/" + rule
                    .RuleId);
                using var stop = await Public(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    new RuleUpdate(
                        current!.Version,
                        current.EquipmentId,
                        current.TargetId,
                        null,
                        "COMM_LOST",
                        current.Severity,
                        null,
                        null,
                        null,
                        null,
                        false
                    ),
                    true
                );
                stop.EnsureSuccessStatusCode();
            }
            if (order is not null)
            {
                var current = await admin.GetFromJsonAsync<OrderView>("/api/v1/orders/" + order
                    .OrderId);
                using var cancel = await Public(
                    admin,
                    "/api/v1/orders/" + order.OrderId + "/cancel",
                    new ReasonCommand(current!.Version, "COMM_LOST verification complete")
                );
                cancel.EnsureSuccessStatusCode();
            }
        }
    }
}


