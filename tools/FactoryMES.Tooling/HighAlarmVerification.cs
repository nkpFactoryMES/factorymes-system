using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Features.Alarms;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class HighAlarmVerification
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
        var instant = CollectionFormat.UtcNow();
        HighReading Reading(
            int age,
            decimal? value,
            string quality = "GOOD"
        ) =>
            new(
                instant.AddSeconds(-age),
                instant.AddSeconds(-age),
                value,
                quality
            );
        bool Evidence(
            HighReading[] values,
            bool recovering = false,
            long hold = 2
        ) =>
            HighThresholdEvidence.Supports(
                values,
                recovering ? 25.8m : 26m,
                recovering,
                hold,
                20,
                instant
            );
        Check(!Evidence([]), "No observations cannot trigger");
        Check(!Evidence([
            Reading(0, 26)
        ]), "One cached observation cannot satisfy hold");
        Check(Evidence([
            Reading(0, 26)
        ], hold: 0), "Trigger equality with zero hold");
        Check(Evidence([
            Reading(0, 26.2m),
            Reading(1, 26.1m),
            Reading(2, 26)
        ]), "Continuous exact hold accepted");
        Check(!Evidence([
            Reading(0, 26),
            Reading(1, 25.9m),
            Reading(2, 26)
        ]), "Deadband breaks trigger hold");
        Check(!Evidence([
            Reading(0, 26),
            Reading(
                1,
                26,
                "BAD"
            ),
            Reading(2, 26)
        ]), "BAD breaks continuity");
        Check(!Evidence([
            Reading(0, 26),
            Reading(
                1,
                26,
                "UNCERTAIN"
            ),
            Reading(2, 26)
        ]), "UNCERTAIN breaks continuity");
        Check(!Evidence([
            Reading(0, null),
            Reading(2, 26)
        ]), "Null metric cannot trigger");
        Check(!Evidence([
            Reading(21, 26),
            Reading(23, 26)
        ]), "Stale latest sample cannot trigger");
        Check(!Evidence([
            new HighReading(
                instant,
                instant.AddSeconds(-21),
                26,
                "GOOD"
            ),
            Reading(2, 26)
        ]),
            "Stale receive cannot trigger");
        Check(!Evidence([
            Reading(0, 26),
            Reading(21, 26)
        ], hold: 21), "Collection gap breaks hold");
        Check(!Evidence([
            Reading(0, 26),
            Reading(-1, 26)
        ]), "Unordered evidence rejected");
        Check(Evidence([
            Reading(0, 25.8m),
            Reading(2, 25.8m)
        ], recovering: true), "Recovery equality accepted");
        Check(!Evidence([
            Reading(0, 25.9m),
            Reading(2, 25.7m)
        ], recovering: true), "Deadband does not recover");
        Check(!Evidence([
            Reading(21, 25),
            Reading(23, 25)
        ], recovering: true), "Stale low data cannot recover");
        Check(!Evidence([
            Reading(0, 25),
            Reading(
                1,
                25,
                "BAD"
            ),
            Reading(2, 25)
        ], recovering: true),
            "Bad low data cannot complete recovery hold");
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
        async Task<HttpResponseMessage> Send(
            HttpClient client,
            string path,
            object body,
            bool put = false
        )
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post, path)
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
                var value = await read();
                if (value is not null) { Check(true, label); return value; }
                await Task.Delay(400);
            }
            throw new InvalidOperationException("Timed out: " + label);
        }
        await Read<SessionUser>(
            await Send(
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
        var me = await Read<SessionUser>(
            await Send(
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
        var key = "H" + Guid.NewGuid().ToString("N")[..12];
        var line = new Line {
            Code = key,
            Name = "HIGH actual collection"
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
        TargetView? target = null;
        RuleView? rule = null;
        OrderView? order = null;
        RuleUpdate Update(RuleView view) => new(
            view.Version,
            view.EquipmentId,
            view.TargetId,
            view.MappingId,
            view.Kind,
            view.Severity,
            view.TriggerHigh,
            view.ClearBelow,
            view.HoldSec,
            view.ClearHoldSec,
            view.Enabled
        );
        try
        {
            var product = await db.Set<Product>().FirstAsync(x => x.Active == EF.Parameter(true));
            order = await Read<OrderView>(
                await Send(
                    admin,
                    "/api/v1/orders",
                    new OrderCreate(
                        product.ProductId.ToString(),
                        equipment.EquipmentId.ToString(),
                        me.UserId,
                        "HIGH actual collection",
                        1,
                        "MANUAL",
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow.AddHours(1),
                        []
                    )
                ),
                201,
                "Create assigned order"
            );
            order = await Read<OrderView>(
                await Send(
                    admin,
                    "/api/v1/orders/" + order.OrderId + "/release",
                    new Command(order.Version)
                ),
                200,
                "Release assigned order"
            );
            var settings = new EquipmentSettings(
                "HTTP",
                "http://equipment-simulator:8080/equipment/" + key,
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
                        "0",
                        true
                    ),
                    new MappingInput(
                        null,
                        "OPERATING_STATE",
                        "operatingState",
                        "STRING",
                        "STATE",
                        null,
                        "1",
                        "0",
                        true
                    )
                ]
            );
            target = await Read<TargetView>(
                await Send(
                    admin,
                    "/api/v1/integration-targets",
                    new EquipmentTargetCreate(
                        key,
                        "HIGH actual collection",
                        "equipment-http",
                        false,
                        "EQUIPMENT",
                        equipment.EquipmentId.ToString(),
                        settings
                    )
                ),
                201,
                "Create disabled target"
            );
            var mapping = target.Config.Content.Settings.Mappings.Single(x => x
                .MetricCode == "TEMPERATURE");
            var state = target.Config.Content.Settings.Mappings.Single(x => x
                .MetricCode == "OPERATING_STATE");
            var write = new RuleWrite(
                equipment.EquipmentId.ToString(),
                key,
                mapping.MappingId,
                "HIGH",
                "WARN",
                "26.0",
                "25.8",
                2,
                2,
                true
            );
            await Expect(
                await Send(
                    worker,
                    "/api/v1/alarm-rules",
                    write
                ),
                403,
                "Worker cannot configure HIGH"
            );
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/alarm-rules", write),
                403,
                "HIGH requires CSRF"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        MappingId = state.MappingId
                    }
                ),
                422,
                "State mapping rejected"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        MappingId = long.MaxValue.ToString()
                    }
                ),
                422,
                "Unrelated mapping rejected"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        ClearBelow = "26"
                    }
                ),
                422,
                "Equal thresholds rejected"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        ClearBelow = "27"
                    }
                ),
                422,
                "Reversed thresholds rejected"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        HoldSec = 3601
                    }
                ),
                422,
                "Oversize hold rejected"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write with {
                        TriggerHigh = "1e3"
                    }
                ),
                422,
                "Noncanonical decimal rejected"
            );
            rule = await Read<RuleView>(
                await Send(
                    admin,
                    "/api/v1/alarm-rules",
                    write
                ),
                201,
                "Create HIGH rule"
            );
            Check(rule.TriggerHigh == "26" && rule.ClearBelow == "25.8" && rule
                .MappingId == mapping.MappingId,
                "Normalized thresholds and mapping returned");
            var sourceSettings = target.Config.Content.Settings;
            await Expect(
                await Send(
                    admin,
                    "/api/v1/integration-targets/" + key + "/configuration",
                    new TargetConfigUpdate(
                        target.Version,
                        target.ConfigVersion,
                        target.DisplayName,
                        false,
                        "Attempt source change",
                        sourceSettings with {
                            Mappings = sourceSettings.Mappings.Select(
                                x => x.MetricCode == "TEMPERATURE" ? x with {
                                    Scale = "2"
                                } : x).ToArray()
                        }
                    ),
                    true
                ),
                409,
                "Enabled HIGH protects conversion definition"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/integration-targets/" + key + "/configuration",
                    new TargetConfigUpdate(
                        target.Version,
                        target.ConfigVersion,
                        target.DisplayName,
                        false,
                        "Attempt mapping removal",
                        sourceSettings with {
                            Mappings = [
                                state
                            ]
                        }
                    ),
                    true
                ),
                409,
                "Enabled HIGH protects omitted mapping"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/integration-targets/" + key + "/configuration",
                    new TargetConfigUpdate(
                        target.Version,
                        target.ConfigVersion,
                        target.DisplayName,
                        false,
                        "Ambiguous mapping identity",
                        sourceSettings with
                        {
                            Mappings = [
                                mapping with {
                                    MetricCode = "TEMP_ALIAS"
                                },
                                mapping with {
                                    MappingId = null
                                },
                                state
                            ]
                        }
                    ),
                    true
                ),
                422,
                "Ambiguous protected mapping returns validation instead of 500"
            );
            var oldUpdate = Update(rule);
            rule = await Read<RuleView>(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    oldUpdate,
                    true
                ),
                200,
                "Inactive episode rule update allowed"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    oldUpdate,
                    true
                ),
                409,
                "HIGH stale version rejected"
            );
            target = await Read<TargetView>(
                await Send(
                    admin,
                    "/api/v1/integration-targets/" + key + "/state",
                    new TargetStateUpdate(
                        target.Version,
                        target.ConfigVersion,
                        true,
                        "Start actual HIGH demo"
                    ),
                    true
                ),
                200,
                "Enable actual collection"
            );
            var ruleId = long.Parse(rule.RuleId);
            var active = await Wait(async () => await db.Set<Alarm>().AsNoTracking()
                .SingleOrDefaultAsync(
                x => x.RuleId == ruleId && x.Status == "ACTIVE"),
                    "Actual Mock ingestion opens HIGH");
            Check(active.Kind == "HIGH" && active.TriggerValue >= 26 && active
                .TriggerSampleAtUtc != null,
                "Trigger value and sample timestamp persisted");
            var snapshot = active.RuleSnapshot;
            Check(JsonDocument.Parse(snapshot).RootElement.GetProperty("triggerHigh")
                .GetString() == "26",
                "Opening threshold snapshot persisted");
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    Update(rule) with {
                        TriggerHigh = "26.2"
                    },
                    true
                ),
                409,
                "ACTIVE rejects threshold reinterpretation"
            );
            await Expect(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    Update(rule) with {
                        HoldSec = 4
                    },
                    true
                ),
                409,
                "ACTIVE rejects hold change"
            );
            rule = await Read<RuleView>(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    Update(rule) with {
                        Severity = "CRITICAL"
                    },
                    true
                ),
                200,
                "Severity edit preserves active definition"
            );
            var visible = await worker.GetFromJsonAsync<AlarmView>("/api/v1/alarms/" + active
                .AlarmId);
            Check(visible!.Severity == "WARN", "Existing alarm severity snapshot immutable");
            var recovered = await Wait(async () => await db.Set<Alarm>().AsNoTracking()
                .SingleOrDefaultAsync(
                x => x.AlarmId == active.AlarmId && x.Status == "RECOVERED"),
                "Actual Mock low samples satisfy recovery hold");
            Check(recovered.RuleSnapshot == snapshot && recovered.RuleVersion == active.RuleVersion
                && recovered.ClosedAtUtc == null && recovered.AckAtUtc == null,
                "Recovery retains snapshot and does not ACK or close");
            Check(await db.Set<Notification>().CountAsync(x => x.AlarmId == active.AlarmId) == 2
                && await db.Set<Notification>().AnyAsync(x => x.AlarmId == active.AlarmId && x
                    .Title == "측정값 상한 초과")
                && await db.Set<Notification>().AnyAsync(x => x.AlarmId == active.AlarmId && x
                    .Title == "측정값 정상 복귀"),
                "HIGH opens and recovers with distinct inbox notifications");
            var notification = await db.Set<Notification>().Where(x => x.AlarmId == active.AlarmId)
                .Select(x => x.NotificationId).FirstAsync();
            Check(await db.Set<NotificationRecipient>().AnyAsync(x => x
                .NotificationId == notification
                && x.UserId == long.Parse(me.UserId)),
                    "Assigned worker receives HIGH notification");
            rule = await Read<RuleView>(
                await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    Update(rule) with {
                        TriggerHigh = "28",
                        ClearBelow = "27"
                    },
                    true
                ),
                200,
                "Recovered rule thresholds editable without rewriting history"
            );
            var events = await db.Set<OutboxEvent>().AsNoTracking().Where(x => x
                .AggregateType == "ALARM"
                && x.AggregateId == active.AlarmId.ToString()).ToArrayAsync();
            Check(events.Length == 2 && events.All(x => x.Status is "PENDING" or "DELIVERED"),
                "Exactly one opening and recovery Outbox retained");
            Directory.CreateDirectory("/reports");
            await File.WriteAllTextAsync("/reports/high-alarms-verification.json", JsonSerializer
                .Serialize(new
            {
                passed = checks.Count,
                checks,
                targetId = key,
                alarmId = active.AlarmId.ToString(),
                snapshot,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0
            }, new JsonSerializerOptions {
                WriteIndented = true
            }));
            Console.WriteLine("HIGH verification passed: " + checks.Count);
        }
        finally
        {
            if (rule is not null)
            {
                var latest = await admin.GetFromJsonAsync<RuleView>("/api/v1/alarm-rules/" + rule
                    .RuleId);
                using var stop = await Send(
                    admin,
                    "/api/v1/alarm-rules/" + rule.RuleId,
                    Update(latest!) with {
                        Enabled = false
                    },
                    true
                );
                stop.EnsureSuccessStatusCode();
            }
            if (target is not null)
            {
                var latest = await admin
                    .GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + key);
                using var stop = await Send(
                    admin,
                    "/api/v1/integration-targets/" + key + "/state",
                    new TargetStateUpdate(
                        latest!.Version,
                        latest.ConfigVersion,
                        false,
                        "HIGH verification complete"
                    ),
                    true
                );
                stop.EnsureSuccessStatusCode();
            }
            if (order is not null)
            {
                var latest = await admin.GetFromJsonAsync<OrderView>("/api/v1/orders/" + order
                    .OrderId);
                using var stop = await Send(
                    admin,
                    "/api/v1/orders/" + order.OrderId + "/cancel",
                    new ReasonCommand(latest!.Version, "HIGH verification complete")
                );
                stop.EnsureSuccessStatusCode();
            }
        }
    }
}


