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

// 로컬 FC03 서버 → 실제 Connector → Gateway/Oracle → AUTO/통계를 검증한다.
// DB 직접 준비는 고유 기준정보뿐이다. 원시 이벤트·실적·상태를 직접 넣지 않는다.
public static class ModbusCollectionVerification
{
    public static async Task RunAsync(MesDbContext db, Func<string, string> secret)
    {
        var checks = new List<string>();
        var faultMode = Environment.GetEnvironmentVariable("MES_MODBUS_FAULT_VERIFICATION") == "1";
        long? beforeResetTotal = null;
        DateTime? recoveryAccept = null;

        async Task Phase(string phase)
        {
            await File.WriteAllTextAsync("/reports/modbus-stage.json",
                JsonSerializer.Serialize(new {
                    stage = phase
                }));
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists("/reports/modbus-ack.txt")
                    && (await File.ReadAllTextAsync("/reports/modbus-ack.txt")).Trim() == phase)
                {
                    return;
                }
                await Task.Delay(150);
            }
            throw new TimeoutException("Modbus controller phase was not acknowledged");
        }

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
        using var anonymous = Client("https://web:8443");
        using var issuer = Client("http://mes-backend:8080");
        using var gateway = Client("http://integration-gateway:8080");
        using var jobs = Client("http://mes-backend:8080");

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
            int expected,
            string label
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == expected,
                    label + " HTTP " + (int)response.StatusCode);
                return (await response.Content.ReadFromJsonAsync<T>())!;
            }
        }

        async Task Expect(
            HttpResponseMessage response,
            int expected,
            string label
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == expected,
                    label + " HTTP " + (int)response.StatusCode);
            }
        }

        async Task Authenticate(
            HttpClient client,
            string identity,
            string audience
        )
        {
            using var response = await issuer.PostAsJsonAsync("/internal/v1/auth/token", new
            {
                clientId = identity,
                clientSecret = secret(identity + "_client_secret"),
                audience
            });
            Check(response.IsSuccessStatusCode, "Service authentication " + identity);
            var token = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", token.GetProperty("accessToken").GetString());
        }

        async Task Wait(
            Func<Task<bool>> ready,
            string label,
            int seconds = 60
        )
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (await ready())
                {
                    Check(true, label);
                    return;
                }
                await Task.Delay(250);
            }
            throw new InvalidOperationException("Timed out: " + label);
        }

        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        var key = "MB" + Guid.NewGuid().ToString("N")[..12];
        var line = new Line {
            Code = key,
            Name = "Modbus verification"
        };
        var product = new Product {
            Code = key,
            Name = "Modbus verification",
            Unit = "EA"
        };
        var equipment = new Equipment {
            Code = key,
            Name = "Modbus verification"
        };
        var other = new Equipment {
            Code = key + "X",
            Name = "Modbus duplicate binding"
        };
        TargetView? target = null;
        string? orderId = null;
        try
        {
            db.AddRange(line, product);
            await db.SaveChangesAsync();
            equipment.LineId = line.LineId;
            other.LineId = line.LineId;
            db.AddRange(equipment, other);
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
            var session = await Read<SessionUser>(
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
            await Authenticate(
                gateway,
                "equipment-connector",
                "integration-gateway"
            );
            await Authenticate(
                jobs,
                "jobs-worker",
                "mes-backend"
            );

            // 다른 설비에 귀속된 unit을 건드리지 않는다. 반복 실행은 빈 테스트 unit만 할당한다.
            var used = await db.Set<EquipmentConnection>().AsNoTracking()
                .Where(x => x.Protocol == ModbusFormat.Protocol)
                .Select(x => x.ApprovedEndpoint).ToListAsync();
            var unit = Enumerable.Range(1, 247).FirstOrDefault(x =>
                !used.Contains("modbus.tcp://modbus-simulator:1502/unit/" + x));
            Check(unit > 0, "Unused local simulator unit allocated");
            var endpoint = "modbus.tcp://modbus-simulator:1502/unit/" + unit;
            MappingInput Mapping(
                string code,
                string source,
                string type = "INTEGER",
                string kind = "NUMBER",
                string scale = "1",
                string? unitLabel = null
            ) => new(
                null,
                code,
                source,
                type,
                kind,
                unitLabel,
                scale,
                "0",
                true
            );
            var settings = new EquipmentSettings(
                ModbusFormat.Protocol,
                endpoint,
                1000,
                1000,
                30,
                null,
                [
                    Mapping(
                        "TOTAL_COUNT",
                        "holding:0:UINT32:ABCD",
                        kind: "COUNTER",
                        unitLabel: "EA"
                    ),
                    Mapping(
                        "TEMPERATURE",
                        "holding:2:INT16:AB",
                        scale: "0.1",
                        unitLabel: "C"
                    ),
                    Mapping(
                        "FLOAT_VALUE",
                        "holding:4:FLOAT32:ABCD",
                        type: "DECIMAL"
                    ),
                    Mapping("WORD_ABCD", "holding:6:UINT32:ABCD"),
                    Mapping("WORD_CDAB", "holding:6:UINT32:CDAB"),
                    Mapping("WORD_BADC", "holding:6:UINT32:BADC"),
                    Mapping("WORD_DCBA", "holding:6:UINT32:DCBA"),
                    Mapping("SIGNED_VALUE", "holding:8:INT16:AB")
                ]
            );
            var create = new EquipmentTargetCreate(
                key,
                "Modbus FC03 verification",
                ModbusFormat.Connector,
                true,
                "EQUIPMENT",
                Id(equipment.EquipmentId),
                settings
            );
            async Task Reject(EquipmentSettings rejected, string label) =>
                await Expect(
                    await Public(
                        admin,
                        "/api/v1/integration-targets",
                        create with {
                            Settings = rejected
                        }
                    ),
                    422,
                    label
                );

            await Expect(
                await Public(
                    anonymous,
                    "/api/v1/integration-targets",
                    create
                ),
                401,
                "Anonymous cannot register equipment"
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/integration-targets",
                    create
                ),
                403,
                "Worker cannot register equipment"
            );
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/integration-targets", create),
                403,
                "Equipment registration requires CSRF"
            );
            await Reject(settings with {
                ApprovedEndpoint = "modbus.tcp://localhost:1502/unit/1"
            },
                "Arbitrary Modbus host rejected");
            await Reject(settings with {
                ApprovedEndpoint = endpoint.Replace(":1502", ":502")
            },
                "Unapproved Modbus port rejected");
            await Reject(settings with {
                ApprovedEndpoint = endpoint + "?debug=1"
            },
                "Endpoint query rejected");
            await Reject(settings with {
                ApprovedEndpoint = endpoint.Replace("/unit/", "/%75nit/")
            },
                "Encoded endpoint path rejected");
            await Reject(settings with {
                CredentialRef = "unapproved-secret"
            },
                "Credential reference rejected for local Modbus");
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create with {
                        ConnectorType = "equipment-http"
                    }
                ),
                422,
                "Connector and protocol mismatch rejected"
            );
            await Reject(settings with
            {
                Mappings = [
                    Mapping("BAD_ORDER", "holding:0:UINT32:AB")
                ]
            }, "Invalid word order rejected");
            await Reject(settings with
            {
                Mappings = [
                    Mapping("BAD_TYPE", "holding:0:FLOAT32:ABCD")
                ]
            }, "Float32 requires DECIMAL metadata");
            await Reject(settings with
            {
                Mappings = [
                    Mapping(
                        "BAD_COUNTER",
                        "holding:0:INT32:ABCD",
                        kind: "COUNTER"
                    )
                ]
            }, "Signed COUNTER mapping rejected");
            await Reject(settings with
            {
                Mappings = [
                    Mapping(
                        "BAD_SCALE",
                        "holding:0:UINT32:ABCD",
                        scale: "0.0000001"
                    )
                ]
            }, "Scale beyond six decimal places rejected");
            await Reject(settings with
            {
                Mappings = [
                    Mapping("FIRST", "holding:0:UINT16:AB"),
                    Mapping("LAST", "holding:125:UINT16:AB")
                ]
            }, "Read span over 125 registers rejected");

            await Reject(settings with
            {
                Mappings = [
                    settings.Mappings[0] with {
                        Scale = "0.1"
                    }
                ]
            }, "Fractional COUNTER scale rejected before polling");
            await Reject(settings with
            {
                Mappings = [
                    settings.Mappings[0] with {
                        Offset = "1"
                    }
                ]
            }, "COUNTER offset rejected before polling");
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create
                ),
                201,
                "Register Modbus target"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create with
                    {
                        TargetId = key + "X",
                        EquipmentId = Id(other.EquipmentId)
                    }
                ),
                409,
                "Physical Modbus unit cannot belong to another equipment"
            );
            var numericTarget = await db.Set<IntegrationTarget>().AsNoTracking()
                .Where(x => x.TargetCode == key).Select(x => x.TargetId).SingleAsync();
            await Wait(async () =>
            {
                var current = await admin.GetFromJsonAsync<TargetView>(
                    "/api/v1/integration-targets/" + key);
                return current?.AppliedConfigVersion == target.ConfigVersion
                    && current.ConnectionState == "CONNECTED"
                    && await db.Set<TelemetryEvent>().AsNoTracking()
                        .AnyAsync(x => x.TargetId == numericTarget);
            }, "Real Modbus collector applied configuration and connected");
            var cached = await gateway.GetFromJsonAsync<EquipmentResolvedConfig>(
                "/internal/v1/configurations/" + key);
            Check(cached?.Content.Settings.Protocol == ModbusFormat.Protocol
                && cached.ConfigHash == target.Config.ConfigHash
                && cached.ConfigHash == CollectionFormat.Hash(cached.Content),
                "Gateway Redis configuration matches Oracle hash and Modbus protocol");
            var page = await admin.GetFromJsonAsync<PageResult<SampleView>>(
                "/api/v1/integration-targets/" + key + "/samples?limit=10");
            var sample = page!.Items.First();
            Check(sample.Metrics.Length == 8 && sample.Metrics.All(x => x.Quality == "GOOD"),
                "One FC03 acquisition persists all eight GOOD mapped metrics");
            decimal Value(string metric) => decimal.Parse(
                sample.Metrics.Single(x => x.MetricCode == metric).NumericValue!,
                CultureInfo.InvariantCulture);
            Check(Value("TOTAL_COUNT") >= unit * 1000, "Unsigned 32-bit counter decoded");
            Check(Value("TEMPERATURE") is >= 25 and <= 34,
                "Signed temperature register uses scale 0.1");
            Check(Value("FLOAT_VALUE") == 123.5m, "IEEE754 FLOAT32 persisted as decimal");
            Check(Value("WORD_ABCD") == 305419896m, "ABCD word order persisted");
            Check(Value("WORD_CDAB") == 1450709556m, "CDAB word order persisted");
            Check(Value("WORD_BADC") == 873625686m, "BADC byte order persisted");
            Check(Value("WORD_DCBA") == 2018915346m, "DCBA reverse order persisted");
            Check(Value("SIGNED_VALUE") == -123, "Signed Int16 remains negative");
            var raw = await db.Set<TelemetryEvent>().AsNoTracking()
                .SingleAsync(x => x.TelemetryId == long.Parse(sample.TelemetryId));
            using var payload = JsonDocument.Parse(raw.Payload);
            Check(payload.RootElement.GetProperty("data").GetProperty("instanceId").GetString()
                == "equipment-connector.local", "Stored event comes from real equipment collector");
            Check(raw.ConfigVersion == target.ConfigVersion
                && raw.OccurredAtUtc.Kind == DateTimeKind.Utc
                && raw.ReceivedAtUtc >= raw.OccurredAtUtc.AddSeconds(-2),
                "Acquisition event keeps configuration and UTC time binding");

            var options = await admin.GetFromJsonAsync<AutoCounterOption[]>(
                "/api/v1/auto-counter-options?equipmentId=" + Id(equipment.EquipmentId));
            var counter = options!.Single(x => x.TargetId == key && x.MetricCode == "TOTAL_COUNT");
            Check(counter.MappingId == sample.Metrics.Single(x => x.MetricCode == "TOTAL_COUNT").MappingId,
                "Modbus counter is selectable for AUTO work order");
            var now = CollectionFormat.UtcNow();
            var order = await Read<OrderView>(
                await Public(
                    admin,
                    "/api/v1/orders",
                    new OrderCreate(
                        Id(product.ProductId),
                        Id(equipment.EquipmentId),
                        session.UserId,
                        "Modbus real counter workflow",
                        100,
                        "AUTO",
                        counter.MappingId,
                        now,
                        now.AddHours(1),
                        [
                            new ChecklistDefinition(
                                1,
                                "Register map verified",
                                true
                            )
                        ]
                    )
                ),
                201,
                "Create AUTO using actual Modbus counter"
            );
            orderId = order.OrderId;
            async Task<OrderView> Current() =>
                (await worker.GetFromJsonAsync<OrderView>("/api/v1/orders/" + orderId))!;
            async Task<OrderView> Act(
                HttpClient client,
                string action,
                object input
            ) =>
                await Read<OrderView>(
                    await Public(
                        client,
                        "/api/v1/orders/" + orderId + "/" + action,
                        input
                    ),
                    200,
                    action
                );
            order = await Act(
                admin,
                "release",
                new Command(order.Version)
            );
            order = await Read<OrderView>(
                await Public(
                    worker,
                    "/api/v1/orders/" + orderId + "/checklist",
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
            order = await Act(
                worker,
                "start",
                new Command(order.Version)
            );
            var commandVersion = order.Version;
            Check(
                order.AutoCounter?.AwaitingBaseline == true,
                "Start waits for next real counter baseline"
            );
            await Wait(
                async () =>
                {
                    order = await Current();
                    return order.AutoCounter?.AwaitingBaseline == false && order.TotalQty >= 3;
                },
                "Real clock-driven Modbus counter produces AUTO quantity",
                20
            );
            Check(
                order.Version == commandVersion,
                "Live Modbus AUTO does not cause command version churn"
            );
            if (faultMode)
            {
                // 실제 통신을 멈춰 새 지표가 만들어지지 않는지 확인한다. 원장/계측은 직접 바꾸지 않는다.
                await Phase("stop-simulator");
                await Wait(
                    async () =>
                    {
                        var runtime = await admin.GetFromJsonAsync<TargetView>(
                            "/api/v1/integration-targets/" + key);
                        return runtime?.ConnectionState == "RETRYING";
                    },
                    "Stopped Modbus server is reported RETRYING",
                    20
                );
                var countStopped = await db.Set<TelemetryEvent>().CountAsync(x => x.TargetId == numericTarget);
                var frozen = (await Current()).TotalQty;
                beforeResetTotal = frozen;
                await Task.Delay(2200);
                Check(await db.Set<TelemetryEvent>().CountAsync(x => x.TargetId == numericTarget)
                    == countStopped, "Disconnected polling does not invent GOOD telemetry");
                await Phase("restart-simulator");
                await Wait(
                    async () =>
                    {
                        order = await Current();
                        var runtime = await admin.GetFromJsonAsync<TargetView>(
                            "/api/v1/integration-targets/" + key);
                        return order.CounterInvalid && runtime?.ConnectionState == "CONNECTED";
                    },
                    "Real server restart reconnects and detects counter reset",
                    25
                );
                Check(
                    order.TotalQty == frozen,
                    "Restart reset does not add negative or guessed production"
                );
                Check(order.Version > commandVersion, "Counter reset advances command version");
                order = await Act(
                    worker,
                    "counter-rebase",
                    new ReasonCommand(order.Version, "Simulator restart physically verified")
                );
                recoveryAccept = order.AutoCounter!.AcceptAfter;
                Check(
                    order.AutoCounter?.AwaitingBaseline == true,
                    "Reset acknowledgment waits for real baseline"
                );
                await Wait(
                    async () =>
                    {
                        order = await Current();
                        return !order.CounterInvalid && order.AutoCounter?.AwaitingBaseline == false
                            && order.TotalQty > frozen;
                    },
                    "Actual Modbus production resumes after rebase",
                    20
                );
            }
            await Expect(
                await Public(
                    worker,
                    "/api/v1/orders/" + orderId + "/entries",
                    new EntryBatch(order.Version,
                    [
                        new EntryInput(
                            Guid.NewGuid().ToString(),
                            "TOTAL",
                            1,
                            null,
                            null,
                            "Must not double count",
                            CollectionFormat.UtcNow()
                        )
                    ])
                ),
                422,
                "Manual TOTAL cannot duplicate live Modbus production"
            );
            order = await Current();
            var rejected = await Read<EntryBatchResult>(
                await Public(
                    worker,
                    "/api/v1/orders/" + orderId + "/entries",
                    new EntryBatch(order.Version,
                    [
                        new EntryInput(
                            Guid.NewGuid().ToString(),
                            "REJECT",
                            1,
                            null,
                            null,
                            "Inspection reject",
                            CollectionFormat.UtcNow()
                        )
                    ])
                ),
                200,
                "Manual reject while Modbus keeps collecting"
            );
            Check(rejected.RejectQty == 1, "Reject is separate from automatic production");
            order = await Current();
            order = await Act(
                worker,
                "submit",
                new SubmitRequest(order.Version, "Actual counter integration verification")
            );
            Check(order.Status == "SUBMITTED" && order.TotalQty >= 3 && order.RejectQty == 1,
                "Live Modbus order submits without retry");
            order = await Act(
                admin,
                "close",
                new ReasonCommand(order.Version, "Counter ledger checked")
            );
            var entries = await worker.GetFromJsonAsync<PageResult<EntryView>>(
                "/api/v1/orders/" + orderId + "/entries?limit=100");
            var automatic = entries!.Items.Where(x => x.Kind == "TOTAL").ToArray();
            Check(automatic.Length > 0 && automatic.All(x => x.Source == "AUTO" && x.TelemetryId is not null),
                "Automatic ledger keeps source and telemetry evidence");
            var numericOrder = long.Parse(orderId, CultureInfo.InvariantCulture);
            var ledger = await db.Set<ProductionEntry>().AsNoTracking()
                .Where(x => x.OrderId == numericOrder && x.Kind == "TOTAL")
                .SumAsync(x => (long?)x.Qty) ?? 0;
            Check(order.Status == "CLOSED" && order.TotalQty == ledger && order.GoodQty == ledger - 1,
                "Closed AUTO totals equal persisted Modbus increment ledger");
            var startRaw = decimal.Parse(JsonSerializer.SerializeToElement(order.StartCounter).GetString()!,
                CultureInfo.InvariantCulture);
            var endRaw = decimal.Parse(JsonSerializer.SerializeToElement(order.EndCounter).GetString()!,
                CultureInfo.InvariantCulture);
            if (!faultMode)
            {
                Check(endRaw - startRaw == ledger, "Uninterrupted real counter difference matches ledger");
            }
            else
            {
                // 리셋이 있으므로 원시 끝-처음이 아니라 리셋 전 원장 + 복구 구간 증가분을 대조한다.
                var accepted = recoveryAccept!.Value;
                var mappingId = long.Parse(counter.MappingId, CultureInfo.InvariantCulture);
                var recoveryRaw = await (
                    from metric in db.Set<TelemetryMetric>().AsNoTracking()
                    join telemetry in db.Set<TelemetryEvent>()
                        on metric.TelemetryId equals telemetry.TelemetryId
                    where telemetry.TargetId == numericTarget && metric.MappingId == mappingId
                        && telemetry.OccurredAtUtc >= accepted && telemetry.ReceivedAtUtc >= accepted
                        && metric.Quality == "GOOD"
                    orderby telemetry.OccurredAtUtc, telemetry.TelemetryId
                    select metric.NumericValue
                ).FirstAsync();
                Check(beforeResetTotal + endRaw - recoveryRaw == ledger,
                    "Reset-separated raw increments match the preserved ledger");
                Check(endRaw < startRaw && ledger > 0,
                    "Real reset allows raw end below start without erasing production");
            }
            var day = CollectionFormat.UtcNow()
                .AddHours(9)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var run = await Read<JobRunView>(
                await jobs.PostAsJsonAsync("/internal/v1/reporting/rebuild",
                    new ReportingRebuildRequest(day)),
                200,
                "Rebuild actual Modbus statistics"
            );
            Check(run.Status == "SUCCEEDED", "Modbus AUTO daily reporting completes");
            DailyProductionView? daily = null;
            string? cursor = null;
            do
            {
                var reports = await admin.GetFromJsonAsync<PageResult<DailyProductionView>>(
                    "/api/v1/reports/daily-production?businessDate=" + day + "&limit=100"
                    + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
                daily = reports!.Items.SingleOrDefault(x => x.EquipmentId == Id(equipment.EquipmentId)
                    && x.ProductId == Id(product.ProductId));
                cursor = reports.NextCursor;
            } while (daily is null && cursor is not null);
            Check(daily is not null && daily.TotalQty == ledger && daily.RejectQty == 1,
                "KST daily report matches actual Modbus AUTO ledger");
            await File.WriteAllTextAsync("/reports/modbus-collection-verification.json",
                JsonSerializer.Serialize(new
                {
                    passed = checks.Count,
                    faultVerified = faultMode,
                    checks,
                    targetId = key,
                    unitId = unit,
                    orderId,
                    observedTotal = ledger,
                    observedReject = 1,
                    actualTcpCollection = true,
                    simulatedPhysicalDevice = true,
                    businessRowsInsertedDirectly = false,
                    rawFixturesInsertedDirectly = false,
                    notCovered = faultMode
                        ? "Real PLC hardware; multi-node/high-load; COMM_LOST/HIGH lifecycle on Modbus."
                        : "Simulator outage/restart; real PLC hardware; multi-node/high-load.",
                    cloudApiCalls = 0,
                    externalDeliveryCalls = 0
                }, new JsonSerializerOptions {
                    WriteIndented = true
                }));
        }
        finally
        {
            // 본 실행의 연결만 중지한다. 실패 원인을 cleanup 충돌로 숨기지 않고 이력은 그대로 둔다.
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
                            "Modbus verification cleanup"
                        ),
                        true
                    );
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Cleanup deferred HTTP " + (int)response.StatusCode);
                    }
                }
            }
        }
    }
}



