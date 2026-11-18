using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class AlarmActionVerification
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
        HttpClient Client() => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (
                _,
                cert,
                _,
                _
            ) => cert?.Thumbprint == pinned.Thumbprint
        }) {
            BaseAddress = new Uri("https://web:8443"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var admin = Client();
        using var worker = Client();
        using var other = Client();
        using var anonymous = Client();
        async Task<HttpResponseMessage> Post(
            HttpClient client,
            string path,
            object body
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
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
        async Task<SessionUser> Login(
            HttpClient client,
            string login,
            string file
        ) =>
            await Read<SessionUser>(
                await Post(
                    client,
                    "/api/v1/auth/login",
                    new {
                        login,
                        password = secret(file)
                    }
                ),
                200,
                login + " login"
            );
        var administrator = await Login(
            admin,
            "admin",
            "admin_password"
        );
        var portal = await Login(
            worker,
            "portal",
            "portal_password"
        );
        var stranger = await Login(
            other,
            "worker.demo",
            "worker_password"
        );
        var key = "ACT" + Guid.NewGuid().ToString("N")[..10];
        var line = new Line {
            Code = key,
            Name = "Alarm action verification"
        };
        db.Add(line);
        await db.SaveChangesAsync();
        var equipment = new Equipment {
            Code = key,
            Name = key,
            LineId = line.LineId
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        var now = CollectionFormat.UtcNow();
        var product = await db.Set<Product>().FirstAsync(x => x.Active == EF.Parameter(true));
        // Only isolated fixture seeding uses DB; transitions under test use public Cookie+CSRF APIs.
        var order = new WorkOrder
        {
            OrderNo = key,
            ProductId = product.ProductId,
            EquipmentId = equipment.EquipmentId,
            AssigneeId = long.Parse(portal.UserId),
            CreatedBy = long.Parse(administrator.UserId),
            Status = "RELEASED",
            CountMode = "MANUAL",
            TargetQty = 1,
            PlannedStartUtc = now,
            PlannedEndUtc = now.AddHours(1)
        };
        var target = new IntegrationTarget
        {
            TargetCode = key,
            Kind = "EQUIPMENT",
            ConnectorType = "equipment-http",
            EquipmentId = equipment.EquipmentId,
            DisplayName = key,
            Enabled = false,
            ConfigVersion = 1
        };
        db.AddRange(order, target);
        await db.SaveChangesAsync();
        db.Add(new EquipmentConnection
        {
            TargetId = target.TargetId,
            Kind = "EQUIPMENT",
            Protocol = "HTTP",
            ApprovedEndpoint = "http://equipment-simulator:8080/equipment/" + key,
            PollIntervalMs = 1000,
            TimeoutMs = 1000,
            StaleSec = 20
        });
        db.Add(new EquipmentTagMapping
        {
            TargetId = target.TargetId,
            MetricCode = "TEMPERATURE",
            SourcePath = "temperature",
            DataType = "DECIMAL",
            ValueKind = "NUMBER",
            Unit = "C",
            Scale = 1,
            OffsetValue = 0,
            Enabled = true
        });
        await db.SaveChangesAsync();
        var rule = new AlarmRule
        {
            EquipmentId = equipment.EquipmentId,
            TargetId = target.TargetId,
            Kind = "COMM_LOST",
            Severity = "WARN",
            Enabled = false
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        Alarm Seed(string status) => new()
        {
            RuleId = rule.RuleId,
            EquipmentId = equipment.EquipmentId,
            Kind = "COMM_LOST",
            Severity = "WARN",
            Status = status,
            RuleVersion = rule.Version,
            RuleSnapshot = "{}",
            OpenedAtUtc = now.AddMinutes(-10),
            RecoveredAtUtc = status == "RECOVERED" ? now.AddMinutes(-1) : null
        };
        var normal = Seed("RECOVERED");
        var forced = Seed("ACTIVE");
        var empty = Seed("RECOVERED");
        db.AddRange(
            normal,
            forced,
            empty
        );
        await db.SaveChangesAsync();
        var path = "/api/v1/alarms/" + normal.AlarmId;
        var forcePath = "/api/v1/alarms/" + forced.AlarmId;
        async Task<AlarmView> Latest(string prefix) => (await admin
            .GetFromJsonAsync<AlarmView>(prefix))!;
        try
        {
            await Expect(
                await anonymous.GetAsync(path + "/actions"),
                401,
                "Anonymous action list denied"
            );
            await Expect(
                await other.GetAsync(path + "/actions"),
                404,
                "Unassigned action list hidden"
            );
            await Expect(
                await Post(
                    other,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        stranger.UserId,
                        "Inspect"
                    )
                ),
                404,
                "Unassigned action create hidden"
            );
            await Expect(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        administrator.UserId,
                        "Inspect"
                    )
                ),
                403,
                "Worker cannot assign another owner"
            );
            await Expect(
                await Post(
                    admin,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        stranger.UserId,
                        "Inspect"
                    )
                ),
                422,
                "Admin cannot assign unrelated worker"
            );
            await Expect(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        portal.UserId,
                        "  "
                    )
                ),
                422,
                "Blank action rejected"
            );
            await Expect(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        portal.UserId,
                        new string('x', 1001)
                    )
                ),
                422,
                "Oversize action rejected"
            );
            await Expect(
                await worker.PostAsJsonAsync(path + "/actions", new ActionCreate(
                    normal.Version,
                    portal.UserId,
                    "Inspect"
                )),
                403,
                "Action requires CSRF"
            );
            var action = await Read<AlarmActionView>(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        portal.UserId,
                        "  Inspect cable  "
                    )
                ),
                201,
                "Worker creates own action"
            );
            Check(action.Status == "OPEN" && action.Content == "Inspect cable" && action
                .AlarmVersion == normal.Version + 1,
                "Create trims content and advances parent version");
            await Expect(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        normal.Version,
                        portal.UserId,
                        "Duplicate"
                    )
                ),
                409,
                "Stale action creation cannot duplicate"
            );
            await Expect(
                await admin.GetAsync(path + "/actions?limit=0"),
                400,
                "Action list bound"
            );
            var paged = await admin
                .GetFromJsonAsync<PageResult<AlarmActionView>>(path + "/actions?limit=1");
            Check(
                paged!.Items.Length == 1 && paged.Items[0].AlarmVersion == action.AlarmVersion,
                "Action list parent version"
            );
            var current = await Latest(path);
            await Expect(
                await Post(
                    admin,
                    path + "/close",
                    new ReasonCommand(current.Version, "Checked")
                ),
                409,
                "Incomplete action blocks normal close"
            );
            await Expect(
                await Post(
                    worker,
                    path + "/close",
                    new ReasonCommand(current.Version, "Checked")
                ),
                403,
                "Worker normal close denied"
            );
            await Expect(
                await Post(
                    worker,
                    forcePath + "/force-close",
                    new ReasonCommand(forced.Version, "Override")
                ),
                403,
                "Worker force close denied"
            );
            var actionPath = "/api/v1/alarm-actions/" + action.ActionId;
            await Expect(
                await Post(
                    worker,
                    actionPath + "/complete",
                    new ActionComplete(
                        action.Version,
                        action.AlarmVersion,
                        "Done"
                    )
                ),
                409,
                "OPEN cannot complete directly"
            );
            await Expect(
                await Post(
                    worker,
                    actionPath + "/start",
                    new ActionStart(action.Version, action.AlarmVersion - 1)
                ),
                409,
                "Start requires current parent version"
            );
            await Expect(
                await Post(
                    other,
                    actionPath + "/start",
                    new ActionStart(action.Version, action.AlarmVersion)
                ),
                404,
                "Out-of-scope start hidden"
            );
            action = await Read<AlarmActionView>(
                await Post(
                    worker,
                    actionPath + "/start",
                    new ActionStart(action.Version, action.AlarmVersion)
                ),
                200,
                "Worker starts action"
            );
            var started = await db.Set<AlarmAction>().AsNoTracking().SingleAsync(x => x
                .ActionId == long.Parse(action.ActionId));
            Check(action.Status == "IN_PROGRESS" && started.StartedAtUtc != null,
                "Start persisted");
            await Expect(
                await Post(
                    worker,
                    actionPath + "/start",
                    new ActionStart(action.Version, action.AlarmVersion)
                ),
                409,
                "Start replay cannot replace start time"
            );
            await Expect(
                await Post(
                    worker,
                    actionPath + "/complete",
                    new ActionComplete(
                        action.Version,
                        action.AlarmVersion,
                        " "
                    )
                ),
                422,
                "Completion needs result"
            );
            action = await Read<AlarmActionView>(
                await Post(
                    worker,
                    actionPath + "/complete",
                    new ActionComplete(
                        action.Version,
                        action.AlarmVersion,
                        "  Cable replaced  "
                    )
                ),
                200,
                "Worker completes action"
            );
            var done = await db.Set<AlarmAction>().AsNoTracking().SingleAsync(x => x
                .ActionId == long.Parse(action.ActionId));
            Check(action.Status == "DONE" && action.Result == "Cable replaced" && done
                .StartedAtUtc == started.StartedAtUtc
                && done.CompletedAtUtc >= done.StartedAtUtc,
                    "Done preserves start and completion ordering");
            await Expect(
                await Post(
                    worker,
                    actionPath + "/complete",
                    new ActionComplete(
                        action.Version,
                        action.AlarmVersion,
                        "Again"
                    )
                ),
                409,
                "Completion replay rejected"
            );
            current = await Latest(path);
            await Expect(
                await Post(
                    admin,
                    path + "/close",
                    new ReasonCommand(current.Version, "Checked")
                ),
                409,
                "Recovery plus done still needs ACK"
            );
            current = await Read<AlarmView>(
                await Post(
                    worker,
                    path + "/ack",
                    new Command(current.Version)
                ),
                200,
                "ACK before normal close"
            );
            await Expect(
                await Post(
                    admin,
                    path + "/close",
                    new ReasonCommand(current.Version, " ")
                ),
                422,
                "Close reason required"
            );
            var closed = await Read<AlarmView>(
                await Post(
                    admin,
                    path + "/close",
                    new ReasonCommand(current.Version, "  Reviewed  ")
                ),
                200,
                "Admin normal close"
            );
            Check(closed.Status == "CLOSED" && closed.CloseKind == "NORMAL" && closed
                .CloseReason == "Reviewed"
                && closed.RecoveredAt != null && closed.AckAt != null,
                    "Normal close evidence preserved");
            await Expect(
                await Post(
                    admin,
                    path + "/close",
                    new ReasonCommand(closed.Version, "Again")
                ),
                409,
                "Closed alarm immutable"
            );
            await Expect(
                await Post(
                    worker,
                    path + "/actions",
                    new ActionCreate(
                        closed.Version,
                        portal.UserId,
                        "Late"
                    )
                ),
                409,
                "No action after close"
            );
            await Expect(
                await Post(
                    admin,
                    actionPath + "/start",
                    new ActionStart(action.Version, closed.Version)
                ),
                409,
                "No action state changes after close"
            );
            current = await Latest(forcePath);
            await Expect(
                await Post(
                    admin,
                    forcePath + "/close",
                    new ReasonCommand(current.Version, "Close")
                ),
                409,
                "ACTIVE cannot normal close"
            );
            var forceAction = await Read<AlarmActionView>(
                await Post(
                    admin,
                    forcePath + "/actions",
                    new ActionCreate(
                        current.Version,
                        administrator.UserId,
                        "Admin inspection"
                    )
                ),
                201,
                "Admin assigns eligible administrator"
            );
            var forceActionPath = "/api/v1/alarm-actions/" + forceAction.ActionId;
            await Expect(
                await Post(
                    worker,
                    forceActionPath + "/start",
                    new ActionStart(forceAction.Version, forceAction.AlarmVersion)
                ),
                403,
                "Visible non-owner cannot start"
            );
            current = await Latest(forcePath);
            await Expect(
                await Post(
                    admin,
                    forcePath + "/force-close",
                    new ReasonCommand(current.Version, "Override")
                ),
                409,
                "Incomplete action blocks forced close"
            );
            forceAction = await Read<AlarmActionView>(
                await Post(
                    admin,
                    forceActionPath + "/start",
                    new ActionStart(forceAction.Version, forceAction.AlarmVersion)
                ),
                200,
                "Admin starts action"
            );
            forceAction = await Read<AlarmActionView>(
                await Post(
                    admin,
                    forceActionPath + "/complete",
                    new ActionComplete(
                        forceAction.Version,
                        forceAction.AlarmVersion,
                        "Inspection documented"
                    )
                ),
                200,
                "Admin completes action"
            );
            current = await Latest(forcePath);
            await Expect(
                await Post(
                    admin,
                    forcePath + "/force-close",
                    new ReasonCommand(current.Version, " ")
                ),
                422,
                "Forced reason required"
            );
            await Expect(
                await Post(
                    admin,
                    forcePath + "/force-close",
                    new ReasonCommand(current.Version - 1, "Override")
                ),
                409,
                "Forced close stale version rejected"
            );
            var before = CollectionFormat.UtcNow();
            var forceClosed = await Read<AlarmView>(
                await Post(
                    admin,
                    forcePath + "/force-close",
                    new ReasonCommand(current.Version, "Maintenance exception")
                ),
                200,
                "Admin force close"
            );
            var savedRule = await db.Set<AlarmRule>().AsNoTracking().SingleAsync(x => x
                .RuleId == rule.RuleId);
            Check(forceClosed.CloseKind == "FORCED" && forceClosed.RecoveredAt == null
                && forceClosed.AckAt == null,
                "Forced close does not invent recovery or ACK");
            Check(savedRule.SuppressUntilUtc >= before.AddMinutes(30)
                && savedRule.SuppressUntilUtc <= CollectionFormat.UtcNow().AddMinutes(30)
                && savedRule.Version == rule.Version + 1,
                    "Rule suppression is 30 minutes and versioned");
            await Expect(
                await Post(
                    admin,
                    forcePath + "/force-close",
                    new ReasonCommand(forceClosed.Version, "Again")
                ),
                409,
                "Forced close replay rejected"
            );
            var emptyPath = "/api/v1/alarms/" + empty.AlarmId;
            var emptyAck = await Read<AlarmView>(
                await Post(
                    worker,
                    emptyPath + "/ack",
                    new Command(empty.Version)
                ),
                200,
                "ACK empty recovered fixture"
            );
            await Expect(
                await Post(
                    admin,
                    emptyPath + "/close",
                    new ReasonCommand(emptyAck.Version, "Close")
                ),
                409,
                "Zero done actions cannot normal close"
            );
            await Expect(
                await Post(
                    admin,
                    emptyPath + "/force-close",
                    new ReasonCommand(emptyAck.Version, "Override")
                ),
                409,
                "Recovered cannot force close"
            );
            var races = await Task.WhenAll(
                Post(
                    worker,
                    emptyPath + "/actions",
                    new ActionCreate(
                        emptyAck.Version,
                        portal.UserId,
                        "Race A"
                    )
                ),
                Post(
                    admin,
                    emptyPath + "/actions",
                    new ActionCreate(
                        emptyAck.Version,
                        administrator.UserId,
                        "Race B"
                    )
                )
            );
            Check(races.Count(x => x.StatusCode == HttpStatusCode.Created) == 1
                && races.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
                "Concurrent creates on one alarm accept exactly one expected version");
            foreach (var response in races) response.Dispose();
            Check(await db.Set<AlarmAction>().CountAsync(x => x.AlarmId == empty.AlarmId) == 1,
                "Concurrent loser leaves no action row");
            Check(await db.Set<AuditEvent>().CountAsync(x => x.EntityId == action.ActionId && x
                .EntityType == "ALARM_ACTION") == 3,
                "Action audit created/started/completed exactly once");
            var closing = await db.Set<OutboxEvent>().AsNoTracking().Where(x => x
                .AggregateType == "ALARM"
                && (x.AggregateId == normal.AlarmId.ToString() || x.AggregateId == forced.AlarmId
                    .ToString())).ToArrayAsync();
            var closingChanges = closing.Select(x => JsonDocument.Parse(x.Payload).RootElement
                .GetProperty("data")
                .GetProperty("change").GetString()).ToArray();
            Check(closingChanges.Count(x => x == "CLOSED") == 1 && closingChanges
                .Count(x => x == "FORCE_CLOSED") == 1,
                "Both close events persisted once");
            Check(await db.Set<Notification>().AnyAsync(x => x.AlarmId == normal.AlarmId && x
                .Title == "경보 정상 종결")
                && await db.Set<Notification>().AnyAsync(x => x.AlarmId == forced.AlarmId && x
                    .Title == "경보 강제 종결"),
                "Closing notifications persisted with correct titles");
            var afterCursor = await admin
                .GetFromJsonAsync<PageResult<AlarmActionView>>(path + "/actions?cursor=" + action
                .ActionId);
            Check(afterCursor!.Items.Length == 0, "Action keyset cursor excludes consumed row");
            var raceWinner = (await admin
                .GetFromJsonAsync<PageResult<AlarmActionView>>(emptyPath + "/actions"))!.Items
                .Single();
            var winnerPath = "/api/v1/alarm-actions/" + raceWinner.ActionId;
            raceWinner = await Read<AlarmActionView>(
                await Post(
                    admin,
                    winnerPath + "/start",
                    new ActionStart(raceWinner.Version, raceWinner.AlarmVersion)
                ),
                200,
                "Finish isolated race winner start"
            );
            raceWinner = await Read<AlarmActionView>(
                await Post(
                    admin,
                    winnerPath + "/complete",
                    new ActionComplete(
                        raceWinner.Version,
                        raceWinner.AlarmVersion,
                        "Race fixture reviewed"
                    )
                ),
                200,
                "Finish isolated race winner completion"
            );
            await Read<AlarmView>(
                await Post(
                    admin,
                    emptyPath + "/close",
                    new ReasonCommand(raceWinner.AlarmVersion, "Race fixture complete")
                ),
                200,
                "Close isolated race fixture"
            );
            var noAction = Seed("ACTIVE");
            db.Add(noAction);
            await db.SaveChangesAsync();
            var noActionClosed = await Read<AlarmView>(
                await Post(
                    admin,
                    "/api/v1/alarms/" + noAction.AlarmId + "/force-close",
                    new ReasonCommand(noAction.Version, "No action administrative exception")
                ),
                200,
                "Forced close permits zero actions as contracted"
            );
            Check(noActionClosed.CloseKind == "FORCED" && noActionClosed.RecoveredAt is null,
                "Zero-action override remains explicitly forced");
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/alarm-actions-verification.json",
                JsonSerializer.Serialize(new
            {
                passed = checks.Count,
                checks,
                fixtureKey = key,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0
            },
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
            Console.WriteLine("Alarm action verification passed: " + checks.Count);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Set<WorkOrder>().Where(x => x.OrderId == order.OrderId && x
                .Status == "RELEASED")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "CANCELLED")
                    .SetProperty(x => x.CancelReason, "Isolated API fixture complete")
                    .SetProperty(x => x.CancelledAtUtc, CollectionFormat.UtcNow()));
        }
    }
}


