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

// 고유 마스터만 DB fixture로 만든다. 작업·계측·원장은 실제 인증 API로 검증하고 보존한다.
public static class AutoProductionVerification
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
        using var cert = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client(string address) => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (
                _,
                peer,
                _,
                _
            ) =>
                peer?.Thumbprint == cert.Thumbprint
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var producer = Client("http://mes-backend:8080");
        using var gateway = Client("http://mes-backend:8080");
        using var jobs = Client("http://mes-backend:8080");
        using var anonymous = Client("http://mes-backend:8080");
        async Task<HttpResponseMessage> Public(
            HttpClient client,
            string route,
            object body,
            bool put = false
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post, route)
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
        async Task Authenticate(HttpClient client, string identity)
        {
            using var response = await anonymous.PostAsJsonAsync("/internal/v1/auth/token", new
            {
                clientId = identity,
                clientSecret = secret(identity + "_client_secret"),
                audience = "mes-backend"
            });
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", value.GetProperty("accessToken").GetString());
        }
        var key = "AUTO" + Guid.NewGuid().ToString("N")[..12];
        var line = new Line {
            Code = key,
            Name = "AUTO verification"
        };
        var product = new Product {
            Code = key,
            Name = "AUTO verification",
            Unit = "EA"
        };
        var equipment = new Equipment {
            Code = key,
            Name = "AUTO verification"
        };
        TargetView? target = null;
        EquipmentResolvedConfig config = null!;
        LeaseResponse? lease = null;
        var stream = "verify." + key;
        long sequence = 0;
        DateTime lastTime = default;
        // 60초 수집 주기의 실제 첫 샘플 뒤 signed fixture를 연속 전송한다.
        async Task<TelemetryBatch> Packet(
            long counter,
            long? seq = null,
            DateTime? stamp = null,
            string? streamId = null
        )
        {
            if (lease is null || lease.ExpiresAt < CollectionFormat.UtcNow().AddSeconds(3))
            {
                lease = await Read<LeaseResponse>(
                    await gateway.PostAsJsonAsync("/internal/v1/integration/leases/renew",
                        new LeaseRequest(
                            key,
                            "equipment-connector.auto-verify",
                            config.Content.ConfigVersion,
                            config.ConfigHash
                        )),
                    200,
                    "Signed collection lease"
                );
            }
            lastTime = stamp ?? CollectionFormat.UtcNow();
            return new TelemetryBatch(
                lease.CollectionAuthorization,
                config,
                [
                    new CollectedEvent(
                        Guid.NewGuid().ToString(),
                        "mes.telemetry.collected.v1",
                        1,
                        "mes.equipment-connector",
                        lastTime,
                        Guid.NewGuid().ToString("N"),
                        new TelemetryData(
                            config.Content.EquipmentId,
                            key,
                            lease.InstanceId,
                            streamId ?? stream,
                            (seq ?? ++sequence).ToString(CultureInfo.InvariantCulture),
                            config.Content.ConfigVersion,
                            config.ConfigHash,
                            [
                                new CollectedMetric(
                                    "TOTAL_COUNT",
                                    config.Content.Settings.Mappings.Single().MappingId!,
                                    counter.ToString(CultureInfo.InvariantCulture),
                                    null,
                                    "EA",
                                    "GOOD"
                                )
                            ]
                        )
                    )
                ]
            );
        }
        async Task<TelemetryBatchAck> Send(TelemetryBatch packet, string label) =>
            await Read<TelemetryBatchAck>(
                await producer.PostAsJsonAsync(
                    "/internal/v1/telemetry/batches", packet),
                200,
                label
            );
        async Task<OrderView> Order(string id) =>
            (await worker.GetFromJsonAsync<OrderView>("/api/v1/orders/" + id))!;
        async Task<OrderView> Act(
            HttpClient client,
            OrderView order,
            string action,
            object body
        ) =>
            await Read<OrderView>(
                await Public(
                    client,
                    "/api/v1/orders/" + order.OrderId + "/" + action,
                    body
                ),
                200,
                action
            );
        async Task Total(
            string id,
            long expected,
            string label
        )
        {
            var current = await Order(id);
            Check(current.TotalQty == expected, label);
            var numeric = long.Parse(id, CultureInfo.InvariantCulture);
            var total = await db.Set<ProductionEntry>().AsNoTracking()
                .Where(x => x.OrderId == numeric && x.Kind == "TOTAL")
                .SumAsync(x => (long?)x.Qty) ?? 0;
            Check(total == expected, label + " persisted ledger");
        }
        async Task<OrderView> Start(OrderCreate input)
        {
            var order = await Read<OrderView>(
                await Public(
                    admin,
                    "/api/v1/orders",
                    input
                ),
                201,
                "Create AUTO draft"
            );
            order = await Act(
                admin,
                order,
                "release",
                new Command(order.Version)
            );
            order = await Read<OrderView>(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/checklist",
                    new ChecklistUpdate(order.Version, order.Checklist.Select(x =>
                        new CheckInput(
                            x.CheckId,
                            true,
                            null
                        )).ToArray()),
                    true
                ),
                200,
                "Checklist"
            );
            return await Act(
                worker,
                order,
                "start",
                new Command(order.Version)
            );
        }
        try
        {
            db.AddRange(line, product);
            await db.SaveChangesAsync();
            equipment.LineId = line.LineId;
            db.Add(equipment);
            await db.SaveChangesAsync();
            await Read<JsonElement>(
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
            var session = await Read<JsonElement>(
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
            await Authenticate(producer, "equipment-connector");
            await Authenticate(gateway, "integration-gateway");
            await Authenticate(jobs, "jobs-worker");
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    new EquipmentTargetCreate(
                        key,
                        "AUTO verification",
                        "equipment-http",
                        true,
                        "EQUIPMENT",
                        equipment.EquipmentId.ToString(CultureInfo.InvariantCulture),
                        new EquipmentSettings(
                            "HTTP",
                            "http://equipment-simulator:8080/equipment/" + key,
                            60000,
                            1000,
                            3600,
                            null,
                            [
                                new MappingInput(
                                    null,
                                    "TOTAL_COUNT",
                                    "totalCount",
                                    "INTEGER",
                                    "COUNTER",
                                    "EA",
                                    "1",
                                    "0",
                                    true
                                )
                            ]
                        )
                    )
                ),
                201,
                "Register Mock target"
            );
            var targetId = await db.Set<IntegrationTarget>().AsNoTracking()
                .Where(x => x.TargetCode == key).Select(x => x.TargetId).SingleAsync();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!await db.Set<TelemetryEvent>().AsNoTracking().AnyAsync(x => x.TargetId == targetId)
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
            Check(await db.Set<TelemetryEvent>().AsNoTracking().AnyAsync(x =>
                x.TargetId == targetId),
                "Real Mock HTTP collector persisted sample");
            var realPayload = await db.Set<TelemetryEvent>().AsNoTracking()
                .Where(x => x.TargetId == targetId).OrderBy(x => x.TelemetryId)
                .Select(x => x.Payload).FirstAsync();
            using var realEvent = JsonDocument.Parse(realPayload);
            Check(realEvent.RootElement.GetProperty("data").GetProperty("instanceId").GetString()
                == "equipment-connector.local", "Real collector identity in stored payload");
            config = (await gateway.GetFromJsonAsync<EquipmentResolvedConfig>(
                "/internal/v1/integration/targets/" + key + "/configuration"))!;
            var create = new OrderCreate(
                product.ProductId.ToString(CultureInfo.InvariantCulture),
                equipment.EquipmentId.ToString(CultureInfo.InvariantCulture),
                session.GetProperty("userId").GetString()!,
                "AUTO boundaries",
                999,
                "AUTO",
                config.Content.Settings.Mappings.Single().MappingId,
                CollectionFormat.UtcNow(),
                CollectionFormat.UtcNow().AddHours(1),
                [
                    new ChecklistDefinition(
                        1,
                        "Counter checked",
                        true
                    )
                ]
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/orders",
                    create with {
                        CounterMappingId = null
                    }
                ),
                422,
                "AUTO requires mapping"
            );
            var order = await Start(create);
            var version = order.Version;
            Check(order.AutoCounter?.AwaitingBaseline == true, "Start arms future baseline");
            var baseline = await Packet(100);
            await Expect(
                await anonymous.PostAsJsonAsync("/internal/v1/telemetry/batches", baseline),
                401,
                "Proof is not service authentication"
            );
            await Expect(
                await gateway.PostAsJsonAsync("/internal/v1/telemetry/batches", baseline),
                403,
                "Gateway cannot produce telemetry"
            );
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches",
                    baseline with {
                        CollectionAuthorization = new string('x', 30)
                    }),
                403,
                "Invalid measurement proof rejected"
            );
            await Send(baseline, "Initial baseline 100");
            await Total(
                order.OrderId,
                0,
                "Initial counter does not become production"
            );
            var increment = await Packet(105);
            var stored = await Send(increment, "Increment 5");
            var replay = await Send(increment, "Replay increment");
            Check(replay.Items.Single().Outcome == "DUPLICATE"
                && replay.Items.Single().TelemetryId == stored.Items.Single().TelemetryId,
                "Exact duplicate returns same telemetry identity");
            await Total(
                order.OrderId,
                5,
                "Replay does not duplicate production"
            );
            order = await Order(order.OrderId);
            Check(order.Version == version, "AUTO collection preserves command version");
            EntryInput Input(
                string kind,
                long qty,
                string? reversal = null
            ) => new(
                Guid.NewGuid().ToString(),
                kind,
                qty,
                reversal,
                null,
                "AUTO verification",
                CollectionFormat.UtcNow()
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/entries",
                    new EntryBatch(order.Version, [
                        Input("TOTAL", 1)
                    ])
                ),
                422,
                "Manual TOTAL rejected"
            );
            var reject = Input("REJECT", 1);
            var rejected = await Read<EntryBatchResult>(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/entries",
                    new EntryBatch(order.Version, [
                        reject
                    ])
                ),
                200,
                "Manual REJECT accepted"
            );
            Check(rejected.RejectQty == 1 && rejected.TotalQty == 5, "REJECT preserves total");
            var duplicate = await Read<EntryBatchResult>(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/entries",
                    new EntryBatch(order.Version, [
                        reject
                    ])
                ),
                200,
                "REJECT replay"
            );
            Check(duplicate.Duplicate && duplicate.RejectQty == 1, "Manual replay stays idempotent");
            var entries = await worker.GetFromJsonAsync<PageResult<EntryView>>(
                "/api/v1/orders/" + order.OrderId + "/entries");
            var automatic = entries!.Items.Single(x => x.Source == "AUTO");
            order = await Order(order.OrderId);
            await Expect(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Input(
                                "REVERSAL",
                                automatic.Qty,
                                automatic.EntryId
                            )
                        ])
                ),
                422,
                "Automatic ledger cannot be manually reversed"
            );
            await Send(await Packet(2), "Counter reset");
            order = await Order(order.OrderId);
            Check(order.CounterInvalid, "Reset marks counter invalid");
            await Send(await Packet(7), "Increase during invalid period");
            await Total(
                order.OrderId,
                5,
                "Invalid period is excluded"
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/orders/" + order.OrderId + "/submit",
                    new SubmitRequest(order.Version, "Must reject invalid")
                ),
                409,
                "Invalid counter blocks submit"
            );
            order = await Order(order.OrderId);
            order = await Act(
                worker,
                order,
                "counter-rebase",
                new ReasonCommand(order.Version, "Physical reset checked")
            );
            Check(!order.CounterInvalid && order.AutoCounter?.AwaitingBaseline == true,
                "Rebase arms future baseline");
            await Send(await Packet(10), "Recovery baseline");
            await Total(
                order.OrderId,
                5,
                "Rebase does not estimate lost quantity"
            );
            await Send(await Packet(13), "Recovery increment 3");
            await Total(
                order.OrderId,
                8,
                "Recovery adds only new increment"
            );
            order = await Order(order.OrderId);
            order = await Act(
                worker,
                order,
                "pause",
                new PauseRequest(
                    order.Version,
                    "VERIFY",
                    "Pause boundary",
                    null
                )
            );
            await Send(await Packet(20), "Paused counter increase");
            await Total(
                order.OrderId,
                8,
                "Paused increase excluded"
            );
            order = await Order(order.OrderId);
            order = await Act(
                worker,
                order,
                "resume",
                new Command(order.Version)
            );
            await Send(await Packet(30), "Resume baseline");
            await Send(await Packet(32), "Resume increment 2");
            await Total(
                order.OrderId,
                10,
                "Resume discards paused gap"
            );
            sequence += 10;
            await Send(await Packet(35), "Sequence gap increment 3");
            await Total(
                order.OrderId,
                13,
                "Gap handled using cumulative delta"
            );
            await Send(await Packet(
                38,
                1,
                streamId: stream + ".new"
            ), "New stream increment 3");
            await Total(
                order.OrderId,
                16,
                "Stream transition preserves cumulative delta"
            );
            var before = lastTime.AddTicks(-10);
            await Send(await Packet(
                999,
                2,
                before,
                stream + ".new"
            ), "Out-of-order timestamp");
            await Total(
                order.OrderId,
                16,
                "Out-of-order event cannot inflate production"
            );
            await Send(await Packet(
                999,
                0,
                streamId: stream + ".new"
            ), "Sequence regression");
            await Total(
                order.OrderId,
                16,
                "Sequence regression cannot inflate production"
            );
            await Send(await Packet(
                38,
                3,
                streamId: stream + ".new"
            ), "Latest sample reconciliation");
            order = await Order(order.OrderId);
            order = await Act(
                worker,
                order,
                "submit",
                new SubmitRequest(order.Version, "Verification below target")
            );
            Check(order.TotalQty == 16 && order.RejectQty == 1 && order.GoodQty == 15,
                "Submit computes net ledger after reset");
            order = await Act(
                admin,
                order,
                "close",
                new ReasonCommand(order.Version, "Reviewed reset evidence")
            );
            Check(order.Status == "CLOSED", "Reset-recovered AUTO closes");
            Check(decimal.Parse(
                JsonSerializer.SerializeToElement(order.EndCounter).GetString()!,
                CultureInfo.InvariantCulture
            )
                < decimal.Parse(
                    JsonSerializer.SerializeToElement(order.StartCounter).GetString()!,
                    CultureInfo.InvariantCulture
                ),
                "Raw end below original start is allowed after reset");
            var second = await Start(create);
            await Send(await Packet(
                50,
                4,
                streamId: stream + ".new"
            ), "Next order baseline");
            await Total(
                second.OrderId,
                0,
                "Next order does not inherit previous quantity"
            );
            await Send(await Packet(
                54,
                5,
                streamId: stream + ".new"
            ), "Next order increment 4");
            await Total(
                second.OrderId,
                4,
                "Next order owns its increment"
            );
            await Total(
                order.OrderId,
                16,
                "Closed predecessor remains unchanged"
            );
            second = await Order(second.OrderId);
            second = await Act(
                worker,
                second,
                "submit",
                new SubmitRequest(second.Version, "Verification below target")
            );
            second = await Act(
                admin,
                second,
                "close",
                new ReasonCommand(second.Version, "Reviewed next order")
            );
            var day = CollectionFormat.UtcNow().AddHours(9)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var run = await Read<JobRunView>(
                await jobs.PostAsJsonAsync(
                    "/internal/v1/reporting/rebuild", new ReportingRebuildRequest(day)),
                200,
                "Rebuild AUTO statistics"
            );
            Check(run.Status == "SUCCEEDED", "AUTO reporting succeeds");
            var daily = await admin.GetFromJsonAsync<PageResult<DailyProductionView>>(
                "/api/v1/reports/daily-production?businessDate=" + day + "&limit=100");
            var row = daily!.Items.Single(x => x.EquipmentId == create.EquipmentId
                && x.ProductId == create.ProductId);
            Check(row.TotalQty == 20 && row.RejectQty == 1, "AUTO daily totals match net ledgers");
            await File.WriteAllTextAsync("/reports/auto-production-verification.json",
                JsonSerializer.Serialize(new
                {
                    passed = checks.Count,
                    checks,
                    targetId = key,
                    orderIds = new[] {
                        order.OrderId,
                        second.OrderId
                    },
                    rawBusinessRowMutation = false,
                    cloudApiCalls = 0,
                    externalDeliveryCalls = 0
                }, new JsonSerializerOptions {
                    WriteIndented = true
                }));
        }
        finally
        {
            // 본 실행의 대상만 비활성화한다. 실패하더라도 원장·계측·감사 이력은 보존한다.
            if (target is not null)
            {
                var current = await admin.GetFromJsonAsync<TargetView>(
                    "/api/v1/integration-targets/" + key);
                if (current?.Enabled == true)
                {
                    using var response = await Public(
                        admin,
                        "/api/v1/integration-targets/" + key + "/state",
                        new TargetStateUpdate(
                            current.Version,
                            current.ConfigVersion,
                            false,
                            "AUTO verification cleanup"
                        ),
                        true
                    );
                    if (!response.IsSuccessStatusCode)
                    {
                        // 실패 원인을 cleanup 409로 덮어쓰지 않는다. 고유 fixture의 이력은 보존한다.
                        Console.WriteLine("Cleanup deferred: fixture target remains enabled HTTP "
                            + (int)response.StatusCode);
                    }
                }
            }
        }
    }
}


