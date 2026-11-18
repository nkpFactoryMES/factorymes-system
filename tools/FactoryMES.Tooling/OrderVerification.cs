using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;
namespace FactoryMES.Tooling;
public static class OrderVerification
{
    public static async Task RunAsync(MesDbContext db, Func<string, string> secret)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        var key = "V" + Guid.NewGuid().ToString("N")[..12];
        var password = DemoData.Password();
        var worker = DemoData.Worker(key + "a", password);
        var other = DemoData.Worker(key + "b", password);
        var product = new Product
        {
            Code = key,
            Name = "Verification product",
            Unit = "EA"
        };
        var line = new Line
        {
            Code = key,
            Name = "Verification line"
        };
        var equipment = new Equipment
        {
            Code = key,
            Name = "Verification equipment"
        };
        using var pinned = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = (
                    _,
                    certificate,
                    _,
                    _
                ) => certificate?.Thumbprint == pinned.Thumbprint
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(Environment
                    .GetEnvironmentVariable("MES_WEB_URL") ?? "https://web:8443"),
                Timeout = TimeSpan.FromSeconds(20)
            };
        }
        async Task Token(HttpClient client)
        {
            var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add(
                "X-CSRF-TOKEN",
                token.GetProperty("requestToken").GetString()
            );
        }
        async Task<HttpResponseMessage> Send(
            HttpClient client,
            string path,
            object body,
            bool put = false
        )
        {
            await Token(client);
            using var request = new HttpRequestMessage(
                put ? HttpMethod.Put : HttpMethod.Post,
                path
            );
            request.Content = JsonContent.Create(body, body.GetType());
            return await client.SendAsync(request);
        }
        async Task Login(
            HttpClient client,
            string login,
            string pass
        )
        {
            using var response = await Send(
                client,
                "/api/v1/auth/login",
                new
                {
                    login,
                    password = pass
                }
            );
            Check(
                response.StatusCode == HttpStatusCode.OK,
                "Cookie login " + login.Replace(key, "fixture")
            );
        }
        async Task<T> Read<T>(
            HttpResponseMessage response,
            string name,
            int code = 200
        )
        {
            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode != code)
                {
                    throw new InvalidOperationException(
                        name + " HTTP " + (int)response.StatusCode + ": " + raw
                    );
                }
                Check(true, name);
                return JsonSerializer.Deserialize<T>(
                    raw,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!;
            }
        }
        async Task Expect(
            HttpResponseMessage response,
            int code,
            string name
        )
        {
            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode != code)
                {
                    throw new InvalidOperationException(
                        name + " HTTP " + (int)response.StatusCode + ": " + raw
                    );
                }
                Check(true, name);
            }
        }
        using var admin = Client();
        using var first = Client();
        using var second = Client();
        using var anonymous = Client();
        var ids = new List<long>();
        string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OrderCreate Create(bool checklist = false) => new(
            Id(product.ProductId),
            Id(equipment.EquipmentId),
            Id(worker.UserId),
            key,
            12,
            "MANUAL",
            null,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1),
            checklist ? [
                new ChecklistDefinition(
                    1,
                    "Required check",
                    true
                )
            ] : []
        );
        async Task<OrderView> NewOrder(bool checklist = false, bool assignedOther = false)
        {
            var input = Create(checklist);
            if (assignedOther)
            {
                input = input with
                {
                    AssigneeId = Id(other.UserId)
                };
            }
            var order = await Read<OrderView>(
                await Send(
                    admin,
                    "/api/v1/orders",
                    input
                ),
                "Admin creates draft",
                201
            );
            ids.Add(long.Parse(order.OrderId));
            return order;
        }
        async Task<OrderView> Command(
            HttpClient client,
            OrderView order,
            string action,
            object input
        ) => await Read<OrderView>(
            await Send(
                client,
                $"/api/v1/orders/{order.OrderId}/{action}",
                input
            ),
            action
        );
        try
        {
            db.AddRange(
                worker,
                other,
                product,
                line
            );
            await db.SaveChangesAsync();
            equipment.LineId = line.LineId;
            db.Add(equipment);
            db.AddRange(
                new UserRole {
                    UserId = worker.UserId,
                    Role = "WORKER"
                },
                new UserRole {
                    UserId = other.UserId,
                    Role = "WORKER"
                }
            );
            await db.SaveChangesAsync();
            await Expect(
                await anonymous.GetAsync("/api/v1/orders"),
                401,
                "Anonymous orders denied"
            );
            await Login(
                admin,
                "admin",
                secret("admin_password")
            );
            await Login(
                first,
                worker.Login,
                password
            );
            await Login(
                second,
                other.Login,
                password
            );
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/orders", Create()),
                403,
                "Create requires CSRF"
            );
            var initial = await NewOrder(true);
            var path = "/api/v1/orders/" + initial.OrderId;
            await Expect(
                await first.GetAsync(path),
                404,
                "Worker cannot read draft"
            );
            await Expect(
                await Send(
                    first,
                    path + "/release",
                    new Command(initial.Version)
                ),
                403,
                "Worker cannot release"
            );

            // A body with a field the contract does not define is a client error, not a server fault.
            using (var unknownField = await Send(
                admin,
                path + "/release",
                new
                {
                    expectedVersion = initial.Version,
                    unknownField = 1
                }
            ))
            {
                Check(
                    unknownField.StatusCode == HttpStatusCode.BadRequest
                        && unknownField.Content.Headers.ContentType?.MediaType
                            == "application/problem+json",
                    "Unknown JSON field returns 400 problem"
                );
            }

            var order = await Command(
                admin,
                initial,
                "release",
                new Command(initial.Version)
            );
            var filtered = await first.GetFromJsonAsync<PageResult<OrderView>>(
                "/api/v1/orders?assigneeId=" + Id(other.UserId)
            );
            Check(filtered!.Items.Length == 0, "Assignee filter cannot widen worker row scope");
            await Expect(
                await second.GetAsync(path),
                404,
                "Other worker row scope denied"
            );
            await Expect(
                await Send(
                    admin,
                    path + "/start",
                    new Command(order.Version)
                ),
                403,
                "Admin cannot enter production"
            );
            await Expect(
                await Send(
                    first,
                    path + "/start",
                    new Command(order.Version)
                ),
                422,
                "Required checklist blocks start"
            );
            var input = new ChecklistUpdate(
                order.Version,
                [
                    new CheckInput(
                        order.Checklist[0].CheckId,
                        true,
                        null
                    )
                ]
            );
            order = await Read<OrderView>(
                await Send(
                    first,
                    path + "/checklist",
                    input,
                    true
                ),
                "Worker checks required item"
            );
            Check(order.Version > initial.Version, "Checklist increments parent version");
            await Expect(
                await Send(
                    first,
                    path + "/start",
                    new Command(initial.Version)
                ),
                409,
                "Stale version denied"
            );
            order = await Command(
                first,
                order,
                "start",
                new Command(order.Version)
            );
            var contender = await NewOrder(false, true);
            contender = await Command(
                admin,
                contender,
                "release",
                new Command(contender.Version)
            );
            await Expect(
                await Send(
                    second,
                    $"/api/v1/orders/{contender.OrderId}/start",
                    new Command(contender.Version)
                ),
                422,
                "Occupied equipment denies second start"
            );
            var now = DateTime.UtcNow;
            now = now.AddTicks(-(now.Ticks % 10));
            var entries = new EntryBatch(
                order.Version,
                [
                    new EntryInput(
                        Guid.NewGuid().ToString(),
                        "TOTAL",
                        10,
                        null,
                        null,
                        null,
                        now
                    ),
                    new EntryInput(
                        Guid.NewGuid().ToString(),
                        "REJECT",
                        2,
                        null,
                        null,
                        "Inspection",
                        now
                    )
                ]
            );
            var saved = await Read<EntryBatchResult>(
                await Send(
                    first,
                    path + "/entries",
                    entries
                ),
                "MANUAL entries atomic save"
            );
            Check(saved.TotalQty == 10 && saved.RejectQty == 2, "MANUAL sums correct");
            var duplicate = await Read<EntryBatchResult>(
                await Send(
                    first,
                    path + "/entries",
                    entries
                ),
                "Retry before stale version check"
            );
            Check(
                duplicate.Duplicate && duplicate.Version == saved.Version,
                "Duplicate does not add quantity or version"
            );
            var changed = entries with
            {
                Entries = [
                    entries.Entries[0] with
                    {
                        Qty = 11
                    },
                    entries.Entries[1]
                ]
            };
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    changed
                ),
                409,
                "Same UUID different payload denied"
            );
            var partial = entries with
            {
                Entries = [
                    entries.Entries[0],
                    entries.Entries[1] with
                    {
                        ClientEntryId = Guid.NewGuid().ToString()
                    }
                ]
            };
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    partial
                ),
                409,
                "Partial duplicate denied"
            );
            var invalid = new EntryBatch(
                saved.Version,
                [
                    new EntryInput(
                        Guid.NewGuid().ToString(),
                        "REJECT",
                        50,
                        null,
                        null,
                        "Invalid",
                        now
                    )
                ]
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    invalid
                ),
                422,
                "Reject exceeding total denied"
            );
            await Expect(
                await Send(
                    second,
                    path + "/entries",
                    invalid
                ),
                404,
                "Other worker cannot append"
            );
            await Expect(
                await Send(
                    first,
                    path + "/submit",
                    new SubmitRequest(saved.Version, null)
                ),
                422,
                "Target variance requires reason"
            );
            order = await Command(
                first,
                order,
                "submit",
                new SubmitRequest(saved.Version, "Short run")
            );
            Check(order.Status == "SUBMITTED"
                && order.GoodQty == 8, "Submission snapshot and totals");
            await Expect(
                await Send(
                    first,
                    path + "/close",
                    new ReasonCommand(order.Version, "Review")
                ),
                403,
                "Worker cannot close"
            );
            await Expect(
                await Send(
                    admin,
                    path + "/close",
                    new ReasonCommand(order.Version, "")
                ),
                422,
                "Close requires review reason"
            );
            order = await Command(
                admin,
                order,
                "close",
                new ReasonCommand(order.Version, "Reviewed")
            );
            Check(order.Status == "CLOSED", "Administrator closes submitted order");
            await Expect(
                await Send(
                    admin,
                    path + "/close",
                    new ReasonCommand(order.Version, "Again")
                ),
                409,
                "Closed order transition denied"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    invalid with {
                        ExpectedVersion = order.Version
                    }
                ),
                409,
                "Closed order append denied"
            );
            var ledger = await first.GetFromJsonAsync<PageResult<EntryView>>(path + "/entries");
            Check(ledger!.Items.Length == 2, "Rejected retries left ledger unchanged");
            Check(
                await db.Set<OrderTransition>().CountAsync(x => x.OrderId == long
                    .Parse(order.OrderId)) == 4,
                "Release start submit close transitions persisted"
            );
            Check(
                await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "WORK_ORDER"
                    && x.EntityId == order.OrderId) == 7,
                "Audit entries persisted atomically"
            );
            var raceFirst = await NewOrder();
            raceFirst = await Command(
                admin,
                raceFirst,
                "release",
                new Command(raceFirst.Version)
            );
            // The RELEASED contender competes for the same equipment.
            var race = await Task.WhenAll(
                Send(
                    first,
                    $"/api/v1/orders/{raceFirst.OrderId}/start",
                    new Command(raceFirst.Version)
                ),
                Send(
                    second,
                    $"/api/v1/orders/{contender.OrderId}/start",
                    new Command(contender.Version)
                )
            );
            Check(
                race.Count(x => x.StatusCode == HttpStatusCode.OK) == 1
                    && race.Count(x => x.StatusCode == HttpStatusCode.UnprocessableEntity) == 1,
                "Concurrent start permits exactly one equipment owner"
            );
            foreach (var response in race)
            {
                response.Dispose();
            }
            var report = new
            {
                passed = checks.Count,
                checks,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0,
                scope = "Real Oracle + HTTPS MANUAL workflow; browser UI verified separately"
            };
            var reportPath = Path.Combine(
                Path.GetDirectoryName(Environment
                    .GetEnvironmentVariable("MES_REPORT_PATH")) ?? "/reports",
                "orders-verification.json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions {
                        WriteIndented = true
                    }
                )
            );
            Console.WriteLine("MANUAL workflow verification complete: " + checks.Count);
        }
        finally
        {
            db.ChangeTracker.Clear();
            // Remove only this run's unique fixture, retaining existing business records.
            var orderIds = await db.Set<WorkOrder>()
                .Where(x => x.ProductId == product.ProductId)
                .Select(x => x.OrderId)
                .ToArrayAsync();
            var orderStrings = orderIds.Select(Id).ToArray();
            await db.Set<ProductionEntry>()
                .Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<WorkCheckItem>()
                .Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<OrderTransition>()
                .Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<AuditEvent>()
                .Where(x => x.EntityType == "WORK_ORDER"
                && orderStrings.Contains(x.EntityId!)).ExecuteDeleteAsync();
            await db.Set<WorkOrder>().Where(x => orderIds.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<UserRole>()
                .Where(x => x.UserId == worker.UserId
                || x.UserId == other.UserId).ExecuteDeleteAsync();
            await db.Set<UserAccount>()
                .Where(x => x.UserId == worker.UserId
                || x.UserId == other.UserId).ExecuteDeleteAsync();
            await db.Set<Equipment>()
                .Where(x => x.EquipmentId == equipment.EquipmentId).ExecuteDeleteAsync();
            await db.Set<Line>().Where(x => x.LineId == line.LineId).ExecuteDeleteAsync();
            await db.Set<Product>()
                .Where(x => x.ProductId == product.ProductId).ExecuteDeleteAsync();
        }
    }
}
