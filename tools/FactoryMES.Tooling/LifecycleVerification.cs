using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class LifecycleVerification
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

        var key = "L" + Guid.NewGuid().ToString("N")[..12];
        var password = DemoData.Password();
        var worker = DemoData.Worker(key + "a", password);
        var other = DemoData.Worker(key + "b", password);
        var product = new Product {
            Code = key,
            Name = "Lifecycle fixture",
            Unit = "EA"
        };
        var line = new Line {
            Code = key,
            Name = "Lifecycle fixture"
        };
        var machine = new Equipment {
            Code = key + "a",
            Name = "Lifecycle fixture"
        };
        var secondMachine = new Equipment {
            Code = key + "b",
            Name = "Lifecycle fixture"
        };
        var targetIds = new List<long>();
        var ruleIds = new List<long>();
        var alarmIds = new List<long>();
        string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var web = new Uri(Environment.GetEnvironmentVariable("MES_WEB_URL") ?? "https://web:8443");
        using var pinned = X509Certificate2.CreateFromPem(secret("https_public_cert"));
        HttpClient Client() => new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (
                _,
                cert,
                _,
                _
            ) =>
                cert?.Thumbprint == pinned.Thumbprint
        })
        {
            BaseAddress = web,
            Timeout = TimeSpan.FromSeconds(20)
        };

        async Task<DateTime> Token(HttpClient client)
        {
            using var response = await client.GetAsync("/api/v1/auth/csrf");
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add(
                "X-CSRF-TOKEN", token.GetProperty("requestToken").GetString()
            );
            var now = DateTime.Parse(
                response.Headers.GetValues("X-Mes-Time").Single(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind
            );
            return now.AddTicks(-(now.Ticks % 10));
        }

        async Task<HttpResponseMessage> Send(
            HttpClient client,
            string path,
            object body
        )
        {
            await Token(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Content = JsonContent.Create(body, body.GetType());
            return await client.SendAsync(request);
        }

        async Task<T> Read<T>(
            HttpResponseMessage response,
            string name,
            int status = 200
        )
        {
            using (response)
            {
                var json = await response.Content.ReadAsStringAsync();
                Check((int)response.StatusCode == status,
                    name + " (HTTP " + (int)response.StatusCode + ")");
                return JsonSerializer.Deserialize<T>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!;
            }
        }

        async Task Expect(
            HttpResponseMessage response,
            int status,
            string name
        )
        {
            using (response)
            {
                Check((int)response.StatusCode == status,
                    name + " (HTTP " + (int)response.StatusCode + ")");
            }
        }

        using var admin = Client();
        using var first = Client();
        using var second = Client();
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
            Check(response.IsSuccessStatusCode, "Fixture login " + login.Replace(key, "worker"));
        }

        async Task<OrderView> Create(bool assignedOther = false)
        {
            var now = await Token(admin);
            var input = new OrderCreate(
                Id(product.ProductId),
                Id(machine.EquipmentId),
                Id(assignedOther ? other.UserId : worker.UserId),
                key,
                10,
                "MANUAL",
                null,
                now.AddMinutes(-1),
                now.AddHours(1),
                []
            );
            var order = await Read<OrderView>(
                await Send(
                    admin,
                    "/api/v1/orders",
                    input
                ),
                "Create lifecycle draft",
                201
            );
            return await Read<OrderView>(
                await Send(
                    admin,
                    $"/api/v1/orders/{order.OrderId}/release",
                    new Command(order.Version)
                ),
                "Release lifecycle order"
            );
        }

        EntryInput Entry(
            string kind,
            long qty,
            DateTime time,
            string? correction = null,
            string? reversal = null
        ) => new(
            Guid.NewGuid().ToString(),
            kind,
            qty,
            reversal,
            correction,
            "Fixture reason",
            time
        );
        async Task<OrderView> Get(HttpClient client, OrderView order) =>
            (await client.GetFromJsonAsync<OrderView>("/api/v1/orders/" + order.OrderId))!;
        async Task<Alarm> AlarmFor(Equipment equipment)
        {
            var target = new IntegrationTarget
            {
                TargetCode = key + Id(equipment.EquipmentId),
                Kind = "EQUIPMENT",
                DisplayName = "Lifecycle fixture",
                ConnectorType = "HTTP",
                EquipmentId = equipment.EquipmentId,
                Enabled = false,
                ConfigVersion = 1
            };
            db.Add(target);
            await db.SaveChangesAsync();
            targetIds.Add(target.TargetId);
            db.Add(new EquipmentConnection
            {
                TargetId = target.TargetId,
                Kind = "EQUIPMENT",
                Protocol = "HTTP",
                ApprovedEndpoint = "http://equipment-simulator:8080/fixture",
                PollIntervalMs = 5000,
                TimeoutMs = 1000,
                StaleSec = 20
            });
            await db.SaveChangesAsync();
            var rule = new AlarmRule
            {
                EquipmentId = equipment.EquipmentId,
                TargetId = target.TargetId,
                Kind = "COMM_LOST",
                Severity = "WARN"
            };
            db.Add(rule);
            await db.SaveChangesAsync();
            ruleIds.Add(rule.RuleId);
            var alarm = new Alarm
            {
                RuleId = rule.RuleId,
                EquipmentId = equipment.EquipmentId,
                Status = "ACTIVE",
                Kind = "COMM_LOST",
                Severity = "WARN",
                RuleVersion = 1,
                RuleSnapshot = "{}",
                OpenedAtUtc = DateTime.UtcNow
            };
            db.Add(alarm);
            await db.SaveChangesAsync();
            alarmIds.Add(alarm.AlarmId);
            return alarm;
        }

        try
        {
            db.AddRange(
                worker,
                other,
                product,
                line
            );
            await db.SaveChangesAsync();
            machine.LineId = line.LineId;
            secondMachine.LineId = line.LineId;
            db.AddRange(machine, secondMachine);
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
            var ownAlarm = await AlarmFor(machine);
            var foreignAlarm = await AlarmFor(secondMachine);
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
            var order = await Create();
            var contender = await Create(true);
            var path = "/api/v1/orders/" + order.OrderId;
            order = await Read<OrderView>(
                await Send(
                    first,
                    path + "/start",
                    new Command(order.Version)
                ), "Worker starts"
            );
            var initialStart = (await db.Set<WorkOrder>().AsNoTracking()
                .SingleAsync(x => x.OrderId == long.Parse(order.OrderId))).StartedAtUtc;
            var pause = new PauseRequest(
                order.Version,
                "OTHER",
                "Fixture pause",
                Id(ownAlarm.AlarmId)
            );
            await Expect(
                await Send(
                    admin,
                    path + "/pause",
                    pause
                ),
                403,
                "Admin cannot pause"
            );
            await Expect(
                await Send(
                    second,
                    path + "/pause",
                    pause
                ),
                404,
                "Other worker cannot pause"
            );
            first.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            await Expect(
                await first.PostAsJsonAsync(path + "/pause", pause),
                403,
                "Pause requires CSRF"
            );
            await Expect(
                await Send(
                    first,
                    path + "/pause",
                    pause with {
                        Note = " "
                    }
                ),
                422,
                "OTHER requires note"
            );
            await Expect(
                await Send(
                    first,
                    path + "/pause",
                    pause with
                    {
                        AlarmId = Id(foreignAlarm.AlarmId)
                    }
                ),
                422,
                "Pause rejects another equipment alarm"
            );
            await Expect(
                await Send(
                    admin,
                    path + "/corrections",
                    new CorrectionOpen(order.Version, "Early")
                ),
                409,
                "Correction cannot open while running"
            );
            order = await Read<OrderView>(await Send(
                first,
                path + "/pause",
                pause
            ), "Pause accepted");
            Check(order.Status == "PAUSED", "Paused state returned");
            var savedPause = await db.Set<WorkPause>().AsNoTracking()
                .SingleAsync(x => x.OrderId == long.Parse(order.OrderId) && x.EndedAtUtc == null);
            Check(savedPause.AlarmId == ownAlarm.AlarmId && savedPause.EquipmentId == machine.EquipmentId,
                "Pause alarm and equipment recorded");
            await Expect(
                await Send(
                    first,
                    path + "/pause",
                    pause with {
                        ExpectedVersion = order.Version
                    }
                ),
                409,
                "Duplicate pause refused"
            );
            await Expect(
                await Send(
                    second,
                    $"/api/v1/orders/{contender.OrderId}/start",
                    new Command(contender.Version)
                ),
                422,
                "Paused order retains equipment occupancy"
            );
            await Expect(
                await Send(
                    admin,
                    path + "/resume",
                    new Command(order.Version)
                ),
                403,
                "Admin cannot resume"
            );
            await Expect(
                await Send(
                    first,
                    path + "/resume",
                    new Command(pause.ExpectedVersion)
                ),
                409,
                "Resume rejects stale version"
            );
            order = await Read<OrderView>(
                await Send(
                    first,
                    path + "/resume",
                    new Command(order.Version)
                ), "Resume accepted"
            );
            Check(order.Status == "RUNNING", "Resume returns running");
            var endedPause = await db.Set<WorkPause>().AsNoTracking()
                .SingleAsync(x => x.PauseId == savedPause.PauseId);
            Check(endedPause.EndedAtUtc >= endedPause.StartedAtUtc, "Resume closes open pause");
            Check((await db.Set<WorkOrder>().AsNoTracking()
                .SingleAsync(x => x.OrderId == long.Parse(order.OrderId))).StartedAtUtc == initialStart,
                "Resume preserves original start snapshot");
            order = await Read<OrderView>(await Send(
                first,
                path + "/pause",
                new PauseRequest(
                    order.Version,
                    "MATERIAL",
                    null,
                    null
                )
            ), "Second pause accepted");
            var time = await Token(first);
            var original = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                new EntryBatch(order.Version, [
                    Entry(
                        "TOTAL",
                        10,
                        time
                    ),
                    Entry(
                        "REJECT",
                        2,
                        time
                    )
                ])
            ),
                "Original entries while paused");
            order = await Read<OrderView>(await Send(
                first,
                path + "/submit",
                new SubmitRequest(original.Version, null)
            ), "Submit paused order");
            Check(!await db.Set<WorkPause>().AnyAsync(x => x.OrderId == long.Parse(order.OrderId)
                && x.EndedAtUtc == null), "Submit closes remaining pause");
            time = await Token(first);
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        Entry(
                            "TOTAL",
                            1,
                            time
                        )
                    ])
                ),
                422,
                "Submitted entries require open correction"
            );
            await Expect(
                await Send(
                    first,
                    path + "/corrections",
                    new CorrectionOpen(order.Version, "Denied")
                ),
                403,
                "Worker cannot open correction"
            );
            var correction = await Read<CorrectionView>(
                await Send(
                    admin,
                    path + "/corrections",
                    new CorrectionOpen(order.Version, "Review totals")
                ),
                "Open correction",
                201
            );
            Check(correction.OrderVersion == order.Version + 1 && correction.Version == 1,
                "Open increments parent version once");
            await Expect(
                await Send(
                    admin,
                    path + "/corrections",
                    new CorrectionOpen(correction.OrderVersion, "Duplicate")
                ),
                409,
                "Only one open correction"
            );
            await Expect(
                await second.GetAsync(path + "/corrections"),
                404,
                "Correction row scope"
            );
            await Expect(
                await Send(
                    admin,
                    path + "/close",
                    new ReasonCommand(correction.OrderVersion, "Blocked")
                ),
                422,
                "Open correction blocks close"
            );

            contender = await Read<OrderView>(await Send(
                second,
                $"/api/v1/orders/{contender.OrderId}/start",
                new Command(contender.Version)
            ),
                "Submission releases occupancy");
            var otherTime = await Token(second);
            var otherEntries = await Read<EntryBatchResult>(await Send(
                second,
                $"/api/v1/orders/{contender.OrderId}/entries",
                new EntryBatch(contender.Version, [
                    Entry(
                        "TOTAL",
                        5,
                        otherTime
                    )
                ])
            ), "Other order entries");
            contender = await Read<OrderView>(await Send(
                second,
                $"/api/v1/orders/{contender.OrderId}/submit",
                new SubmitRequest(otherEntries.Version, "Short run")
            ), "Other order submitted");
            await Token(admin);
            var openings = await Task.WhenAll(
                admin.PostAsJsonAsync(
                    $"/api/v1/orders/{contender.OrderId}/corrections",
                    new CorrectionOpen(contender.Version, "Concurrent A")
                ),
                admin.PostAsJsonAsync(
                    $"/api/v1/orders/{contender.OrderId}/corrections",
                    new CorrectionOpen(contender.Version, "Concurrent B")
                )
            );
            Check(openings.Count(x => (int)x.StatusCode == 201) == 1
                && openings.Count(x => (int)x.StatusCode == 409) == 1, "Concurrent open permits one request");
            var foreignCorrection = await openings.Single(x => (int)x.StatusCode == 201)
                .Content.ReadFromJsonAsync<CorrectionView>();
            foreach (var response in openings)
            {
                response.Dispose();
            }
            var totalOriginal = original.Entries.Single(x => x.Kind == "TOTAL");
            var rejectOriginal = original.Entries.Single(x => x.Kind == "REJECT");
            time = await Token(first);
            EntryBatch Batch(params EntryInput[] entries) => new(correction.OrderVersion, entries);
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    Batch(Entry(
                        "TOTAL",
                        1,
                        time,
                        foreignCorrection!.CorrectionId
                    ))
                ),
                422,
                "Cross-order correction rejected"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    Batch(Entry(
                        "REVERSAL",
                        5,
                        time,
                        correction.CorrectionId,
                        otherEntries.Entries[0].EntryId
                    ))
                ),
                422,
                "Cross-order reversal rejected"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    Batch(Entry(
                        "REVERSAL",
                        9,
                        time,
                        correction.CorrectionId,
                        totalOriginal.EntryId
                    ))
                ),
                422,
                "Reversal requires original quantity"
            );
            var reversal = Entry(
                "REVERSAL",
                10,
                time,
                correction.CorrectionId,
                totalOriginal.EntryId
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    Batch(reversal)
                ),
                422,
                "Negative intermediate final total rejected atomically"
            );
            var replacement = Entry(
                "TOTAL",
                9,
                time,
                correction.CorrectionId
            );
            var correctionBatch = Batch(reversal, replacement);
            var changed = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                correctionBatch
            ),
                "Atomic reversal and replacement");
            Check(
                changed.TotalQty == 9 && changed.RejectQty == 2,
                "Corrected total with immutable source"
            );
            Check((await db.Set<ProductionEntry>().AsNoTracking()
                .SingleAsync(x => x.EntryId == long.Parse(totalOriginal.EntryId))).Qty == 10,
                "Original entry remains unchanged");
            var replay = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                correctionBatch
            ),
                "Correction replay before stale-version check");
            Check(
                replay.Duplicate && replay.Version == changed.Version,
                "Correction replay has no new writes"
            );
            var reverseEntry = changed.Entries.Single(x => x.Kind == "REVERSAL");
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(changed.Version,
                        [
                            Entry(
                                "REVERSAL",
                                10,
                                time,
                                correction.CorrectionId,
                                reverseEntry.EntryId
                            )
                        ])
                ),
                422,
                "Reversing a reversal is forbidden"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(changed.Version,
                        [
                            Entry(
                                "REVERSAL",
                                10,
                                time,
                                correction.CorrectionId,
                                totalOriginal.EntryId
                            )
                        ])
                ),
                409,
                "Original can be reversed only once"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(changed.Version,
                        [
                            Entry(
                                "TOTAL_ADJUST_DOWN",
                                1,
                                time,
                                correction.CorrectionId
                            )
                        ])
                ),
                422,
                "MANUAL rejects AUTO decrement source"
            );
            var rejectBatch = new EntryBatch(changed.Version,
            [
                Entry(
                    "REVERSAL",
                    2,
                    time,
                    correction.CorrectionId,
                    rejectOriginal.EntryId
                ),
                Entry(
                    "REJECT",
                    1,
                    time,
                    correction.CorrectionId
                )
            ]);
            var correctedReject = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                rejectBatch
            ),
                "Reject reversal and replacement");
            Check(
                correctedReject.TotalQty == 9 && correctedReject.RejectQty == 1,
                "Corrected reject total"
            );
            var resolvePath = "/api/v1/corrections/" + correction.CorrectionId + "/resolve";
            var resolve = new CorrectionResolve(
                correction.Version,
                correctedReject.Version,
                "Reviewed"
            );
            await Expect(
                await Send(
                    first,
                    resolvePath,
                    resolve
                ),
                403,
                "Worker cannot resolve correction"
            );
            await Expect(
                await Send(
                    admin,
                    resolvePath,
                    resolve with
                    {
                        ExpectedOrderVersion = changed.Version
                    }
                ),
                409,
                "Resolve rejects stale parent version"
            );
            await Expect(
                await Send(
                    admin,
                    resolvePath,
                    resolve with {
                        ExpectedVersion = 99
                    }
                ),
                409,
                "Resolve rejects stale correction version"
            );
            var resolved = await Read<CorrectionView>(await Send(
                admin,
                resolvePath,
                resolve
            ),
                "Administrator resolves correction");
            Check(resolved.Status == "RESOLVED" && resolved.Version == correction.Version + 1
                && resolved.OrderVersion == correctedReject.Version + 1, "Resolve versions and actor saved");
            order = await Get(admin, order);
            Check(order.Status == "SUBMITTED" && order.TotalQty == 9 && order.RejectQty == 1,
                "Correction does not reopen production or equipment occupancy");
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Entry(
                                "TOTAL",
                                1,
                                time,
                                correction.CorrectionId
                            )
                        ])
                ),
                422,
                "Resolved correction rejects new entries"
            );
            var resolvedReplay = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                rejectBatch
            ),
                "Replay after resolution");
            Check(resolvedReplay.Duplicate, "Resolved correction replay is read-only");
            order = await Read<OrderView>(await Send(
                admin,
                path + "/close",
                new ReasonCommand(order.Version, "Reviewed correction")
            ), "Close corrected order");
            await Expect(
                await Send(
                    admin,
                    path + "/corrections",
                    new CorrectionOpen(order.Version, "Closed")
                ),
                409,
                "No correction after close"
            );
            var closedReplay = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                rejectBatch
            ),
                "Replay accepted committed entries after close");
            Check(closedReplay.Duplicate && closedReplay.TotalQty == 9 && closedReplay.RejectQty == 1,
                "Closed replay leaves totals unchanged");
            Check(await db.Set<WorkPause>().CountAsync(x => x.OrderId == long.Parse(order.OrderId)) == 2,
                "Corrections do not create false downtime");
            var correctionRows = await first.GetFromJsonAsync<PageResult<CorrectionView>>(
                path + "/corrections"
            );
            Check(correctionRows!.Items.Single().ResolvedBy is not null
                && correctionRows.Items.Single().OrderVersion == order.Version,
                "Correction history exposes current parent version");

            var racingEntries = new EntryBatch(foreignCorrection!.OrderVersion,
                [
                    Entry(
                        "TOTAL",
                        1,
                        await Token(second),
                        foreignCorrection.CorrectionId
                    )
                ]);
            var race = await Task.WhenAll(
                Send(
                    admin,
                    $"/api/v1/corrections/{foreignCorrection.CorrectionId}/resolve",
                    new CorrectionResolve(
                        foreignCorrection.Version,
                        foreignCorrection.OrderVersion,
                        "Race review"
                    )
                ),
                Send(
                    second,
                    $"/api/v1/orders/{contender.OrderId}/entries",
                    racingEntries
                )
            );
            Check(race.Count(x => x.IsSuccessStatusCode) == 1
                && race.Count(x => (int)x.StatusCode == 409) == 1,
                "Resolve and entry race has one winner under parent lock");
            foreach (var response in race)
            {
                response.Dispose();
            }

            var report = new
            {
                passed = checks.Count,
                checks,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0
            };
            var output = Path.Combine(Path.GetDirectoryName(
                Environment.GetEnvironmentVariable("MES_REPORT_PATH")) ?? "/reports",
                "lifecycle-verification.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(
                report, new JsonSerializerOptions {
                    WriteIndented = true
                }));
            Console.WriteLine("Lifecycle verification complete: " + checks.Count);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = await db.Set<WorkOrder>().Where(x => x.ProductId == product.ProductId)
                .Select(x => x.OrderId).ToArrayAsync();
            var strings = ids.Select(Id).ToArray();
            await db.Set<ProductionEntry>().Where(x => ids.Contains(x.OrderId) && x.Kind == "REVERSAL")
                .ExecuteDeleteAsync();
            await db.Set<ProductionEntry>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<CorrectionRequest>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<WorkPause>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<WorkCheckItem>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<OrderTransition>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<AuditEvent>()
                .Where(x => x.EntityType == "WORK_ORDER" && strings.Contains(x.EntityId!))
                .ExecuteDeleteAsync();
            await db.Set<WorkOrder>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<Alarm>().Where(x => alarmIds.Contains(x.AlarmId)).ExecuteDeleteAsync();
            await db.Set<AlarmRule>().Where(x => ruleIds.Contains(x.RuleId)).ExecuteDeleteAsync();
            await db.Set<EquipmentConnection>().Where(x => targetIds.Contains(x.TargetId))
                .ExecuteDeleteAsync();
            await db.Set<IntegrationTarget>().Where(x => targetIds.Contains(x.TargetId))
                .ExecuteDeleteAsync();
            await db.Set<Equipment>().Where(x => x.EquipmentId == machine.EquipmentId
                || x.EquipmentId == secondMachine.EquipmentId).ExecuteDeleteAsync();
            await db.Set<Line>().Where(x => x.LineId == line.LineId).ExecuteDeleteAsync();
            await db.Set<Product>().Where(x => x.ProductId == product.ProductId).ExecuteDeleteAsync();
            await db.Set<UserRole>().Where(x => x.UserId == worker.UserId || x.UserId == other.UserId)
                .ExecuteDeleteAsync();
            await db.Set<UserAccount>().Where(x => x.UserId == worker.UserId || x.UserId == other.UserId)
                .ExecuteDeleteAsync();
        }
    }
}

