using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class PreSubmitReversalVerification
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

        var key = "R" + Guid.NewGuid().ToString("N")[..12];
        var password = DemoData.Password();
        var worker = DemoData.Worker(key + "a", password);
        var other = DemoData.Worker(key + "b", password);
        var product = new Product {
            Code = key,
            Name = "Pre-submit reversal fixture",
            Unit = "EA"
        };
        var line = new Line {
            Code = key,
            Name = "Pre-submit reversal fixture"
        };
        var machine = new Equipment {
            Code = key + "a",
            Name = "Pre-submit reversal fixture"
        };
        var secondMachine = new Equipment {
            Code = key + "b",
            Name = "Pre-submit reversal fixture"
        };
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
                Id(assignedOther ? secondMachine.EquipmentId : machine.EquipmentId),
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
        async Task<EntryBatchResult> Append(
            HttpClient client,
            OrderView order,
            EntryInput[] entries
        ) => await Read<EntryBatchResult>(
            await Send(
                client,
                "/api/v1/orders/" + order.OrderId + "/entries",
                new EntryBatch(order.Version, entries)
            ),
            "Append fixture batch"
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
            var foreign = await Create(true);
            var path = "/api/v1/orders/" + order.OrderId;
            var foreignPath = "/api/v1/orders/" + foreign.OrderId;
            order = await Read<OrderView>(await Send(
                first,
                path + "/start",
                new Command(order.Version)
            ), "Start own work");
            foreign = await Read<OrderView>(await Send(
                second,
                foreignPath + "/start",
                new Command(foreign.Version)
            ), "Start foreign work");
            var cross = await Append(
                second,
                foreign,
                [
                    Entry(
                        "TOTAL",
                        5,
                        await Token(second)
                    )
                ]
            );
            var initial = await Append(
                first,
                order,
                [
                    Entry(
                        "TOTAL",
                        10,
                        await Token(first)
                    ),
                    Entry(
                        "REJECT",
                        2,
                        await Token(first)
                    )
                ]
            );
            order = await Get(first, order);
            var totalEntry = initial.Entries.Single(x => x.Kind == "TOTAL");
            var rejectEntry = initial.Entries.Single(x => x.Kind == "REJECT");
            var original = await db.Set<ProductionEntry>().AsNoTracking()
                .SingleAsync(x => x.EntryId == long.Parse(rejectEntry.EntryId));
            var cancel = Entry(
                "REVERSAL",
                2,
                await Token(first),
                reversal: rejectEntry.EntryId
            );
            var batch = new EntryBatch(order.Version, [
                cancel
            ]);
            await Expect(
                await Send(
                    admin,
                    path + "/entries",
                    batch
                ),
                403,
                "Administrator cannot reverse production"
            );
            await Expect(
                await Send(
                    second,
                    path + "/entries",
                    batch
                ),
                404,
                "Another worker cannot reverse this order"
            );
            using (var noCsrf = Client())
            {
                await Login(
                    noCsrf,
                    worker.Login,
                    password
                );
                noCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                await Expect(
                    await noCsrf.PostAsJsonAsync(path + "/entries", batch),
                    403,
                    "Reversal requires CSRF"
                );
            }
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version - 1, [
                        cancel
                    ])
                ),
                409,
                "Reversal rejects stale parent version"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        cancel with {
                            Reason = " "
                        }
                    ])
                ),
                422,
                "Reversal reason required"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        cancel with {
                            Qty = 1
                        }
                    ])
                ),
                422,
                "Full original quantity required"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        cancel with {
                            CorrectionId = "1"
                        }
                    ])
                ),
                422,
                "Pre-submit reversal cannot link administrator correction"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Entry(
                                "REVERSAL",
                                5,
                                await Token(first),
                                reversal: cross.Entries[0].EntryId
                            )
                        ])
                ),
                422,
                "Cross-order original rejected"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Entry(
                                "REVERSAL",
                                10,
                                await Token(first),
                                reversal: totalEntry.EntryId
                            )
                        ])
                ),
                422,
                "Reversal cannot leave reject greater than total"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            cancel,
                            cancel with {
                                ClientEntryId = Guid.NewGuid().ToString()
                            }
                        ])
                ),
                409,
                "Duplicate original in one batch rejected"
            );
            var beforeRejected = await Get(first, order);
            Check(beforeRejected.Version == order.Version && beforeRejected.TotalQty == 10
                && beforeRejected.RejectQty == 2,
                "Rejected batches leave version and totals unchanged");
            var reversed = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                batch
            ),
                "RUNNING own reject reversal accepted");
            Check(!reversed.Duplicate && reversed.Version == order.Version + 1
                && reversed.TotalQty == 10 && reversed.RejectQty == 0,
                "Running reversal updates derived sums and one parent version");
            var after = await db.Set<ProductionEntry>().AsNoTracking()
                .SingleAsync(x => x.EntryId == original.EntryId);
            Check(original.Qty == after.Qty && original.Kind == after.Kind
                && original.RecordedBy == after.RecordedBy && original.Reason == after.Reason
                && original.RequestHash == after.RequestHash
                && original.OccurredAtUtc == after.OccurredAtUtc,
                "Original entry remains immutable");
            var reversalId = long.Parse(reversed.Entries[0].EntryId);
            var stored = await db.Set<ProductionEntry>().AsNoTracking()
                .SingleAsync(x => x.EntryId == reversalId);
            Check(stored.CorrectionId is null && stored.RecordedBy == worker.UserId
                && stored.ReversalOf == original.EntryId && stored.Reason == cancel.Reason,
                "Pre-submit reversal stores actor reason original without correction request");
            var replay = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                batch
            ),
                "Unknown-response same UUID replay accepted");
            Check(replay.Duplicate && replay.Version == reversed.Version
                && replay.Entries.Single().EntryId == reversed.Entries.Single().EntryId,
                "Replay has no second reversal or version change");
            order = await Get(first, order);
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        cancel with {
                            Reason = "Different payload"
                        }
                    ])
                ),
                409,
                "Same UUID different payload rejected"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            cancel,
                            Entry(
                                "TOTAL",
                                1,
                                await Token(first)
                            )
                        ])
                ),
                409,
                "Partially duplicated batch rejected"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            cancel with {
                                ClientEntryId = Guid.NewGuid().ToString()
                            }
                        ])
                ),
                409,
                "Original can only be reversed once"
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Entry(
                                "REVERSAL",
                                2,
                                await Token(first),
                                reversal: Id(reversalId)
                            )
                        ])
                ),
                422,
                "Reversal of reversal rejected"
            );

            var addedReject = await Append(
                first,
                order,
                [
                    Entry(
                        "REJECT",
                        3,
                        await Token(first)
                    )
                ]
            );
            order = await Get(first, order);
            order = await Read<OrderView>(await Send(
                first,
                path + "/pause",
                new PauseRequest(
                    order.Version,
                    "MATERIAL",
                    null,
                    null
                )
            ), "Pause work");
            var pair = new EntryBatch(order.Version,
            [
                Entry(
                    "REVERSAL",
                    10,
                    await Token(first),
                    reversal: totalEntry.EntryId
                ),
                Entry(
                    "REVERSAL",
                    3,
                    await Token(first),
                    reversal: addedReject.Entries[0].EntryId
                )
            ]);
            var paired = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                pair
            ),
                "PAUSED total and reject reversal committed atomically");
            Check(paired.TotalQty == 0 && paired.RejectQty == 0,
                "Final batch invariant allows cancelling total and reject together");
            Check(await db.Set<WorkPause>().AnyAsync(x => x.OrderId == long.Parse(order.OrderId)
                && x.EndedAtUtc == null), "Reversal does not resume paused work");
            order = await Get(first, order);
            await Append(
                first,
                order,
                [
                    Entry(
                        "TOTAL",
                        10,
                        await Token(first)
                    ),
                    Entry(
                        "REJECT",
                        2,
                        await Token(first)
                    )
                ]
            );
            order = await Get(first, order);
            // Race two new UUID requests against one original under the same parent version.
            var raceOriginal = await db.Set<ProductionEntry>().AsNoTracking()
                .Where(x => x.OrderId == long.Parse(order.OrderId) && x.Kind == "REJECT")
                .OrderByDescending(x => x.EntryId).FirstAsync();
            await Token(first);
            using var raceA = JsonContent.Create(new EntryBatch(order.Version,
                [
                    Entry(
                        "REVERSAL",
                        2,
                        await Token(first),
                        reversal: Id(raceOriginal.EntryId)
                    )
                ]));
            using var raceB = JsonContent.Create(new EntryBatch(order.Version,
                [
                    Entry(
                        "REVERSAL",
                        2,
                        await Token(first),
                        reversal: Id(raceOriginal.EntryId)
                    )
                ]));
            var race = await Task.WhenAll(
                first.PostAsync(path + "/entries", raceA),
                first.PostAsync(path + "/entries", raceB)
            );
            Check(race.Count(x => x.IsSuccessStatusCode) == 1
                && race.Count(x => (int)x.StatusCode == 409) == 1,
                "Concurrent reversal has exactly one winner");
            foreach (var response in race)
            {
                response.Dispose();
            }
            Check(await db.Set<ProductionEntry>()
                .CountAsync(x => x.ReversalOf == raceOriginal.EntryId) == 1,
                "Concurrent reversal leaves exactly one ledger reversal");
            order = await Get(first, order);
            order = await Read<OrderView>(await Send(
                first,
                path + "/submit",
                new SubmitRequest(order.Version, null)
            ), "Submit work after pre-submit cancellations");
            var submittedReplay = await Read<EntryBatchResult>(
                await Send(
                    first,
                    path + "/entries",
                    batch
                ), "Committed replay after submission");
            Check(submittedReplay.Duplicate && submittedReplay.Version == order.Version,
                "Submission replay is read-only");
            var latestTotal = await db.Set<ProductionEntry>().AsNoTracking()
                .Where(x => x.OrderId == long.Parse(order.OrderId) && x.Kind == "TOTAL")
                .OrderByDescending(x => x.EntryId).FirstAsync();
            var postSubmit = Entry(
                "REVERSAL",
                latestTotal.Qty,
                await Token(first),
                reversal: Id(latestTotal.EntryId)
            );
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version, [
                        postSubmit
                    ])
                ),
                422,
                "SUBMITTED still requires administrator OPEN correction"
            );
            var correction = await Read<CorrectionView>(
                await Send(
                    admin,
                    path + "/corrections",
                    new CorrectionOpen(order.Version, "Verify existing administrator correction")
                ),
                "Administrator opens correction",
                201
            );
            order = await Get(first, order);
            var corrected = await Append(
                first,
                order,
                [
                    postSubmit with {
                        CorrectionId = correction.CorrectionId
                    }
                ]
            );
            order = await Get(first, order);
            Check(corrected.TotalQty == 0 && corrected.RejectQty == 0,
                "Existing submitted correction reversal preserved");
            var auditCount = await db.Set<AuditEvent>().CountAsync(x => x.EntityType == "WORK_ORDER"
                && x.EntityId == order.OrderId && x.Action == "order.entries");
            Check(auditCount == 7, "Only committed batches produce entry audit events");
            await Read<CorrectionView>(await Send(
                admin,
                "/api/v1/corrections/" + correction.CorrectionId + "/resolve",
                new CorrectionResolve(
                    correction.Version,
                    order.Version,
                    "Reviewed corrected ledger"
                )
            ),
                "Administrator resolves correction");
            order = await Get(first, order);
            order = await Read<OrderView>(await Send(
                admin,
                path + "/close",
                new ReasonCommand(order.Version, "Verified close")
            ), "Administrator closes work");
            var closedReplay = await Read<EntryBatchResult>(await Send(
                first,
                path + "/entries",
                batch
            ),
                "Committed replay after close");
            Check(closedReplay.Duplicate && closedReplay.Version == order.Version,
                "Closed replay leaves closed state unchanged");
            await Expect(
                await Send(
                    first,
                    path + "/entries",
                    new EntryBatch(order.Version,
                        [
                            Entry(
                                "TOTAL",
                                1,
                                await Token(first)
                            )
                        ])
                ),
                409,
                "Closed rejects new production"
            );

            // A same-order original written by another actor must not be cancellable before submit.
            // Seed this isolated ledger row directly to exercise the ownership guard.
            var actorOrder = await Create();
            var actorPath = "/api/v1/orders/" + actorOrder.OrderId;
            actorOrder = await Read<OrderView>(await Send(
                first,
                actorPath + "/start",
                new Command(actorOrder.Version)
            ), "Start ownership fixture");
            var foreignActorEntry = new ProductionEntry
            {
                ClientEntryId = Guid.NewGuid().ToString(),
                OrderId = long.Parse(actorOrder.OrderId),
                Kind = "TOTAL",
                Qty = 1,
                RecordedBy = other.UserId,
                OccurredAtUtc = await Token(first),
                RequestHash = new string('A', 64)
            };
            db.Add(foreignActorEntry);
            await db.SaveChangesAsync();
            await Expect(
                await Send(
                    first,
                    actorPath + "/entries",
                    new EntryBatch(actorOrder.Version,
                        [
                            Entry(
                                "REVERSAL",
                                1,
                                await Token(first),
                                reversal: Id(foreignActorEntry.EntryId)
                            )
                        ])
                ),
                422,
                "Same-order original owned by another actor rejected"
            );
            Check(await db.Set<ProductionEntry>()
                .CountAsync(x => x.OrderId == foreignActorEntry.OrderId) == 1,
                "Ownership rejection leaves original intact");

            var report = new
            {
                passed = checks.Count,
                checks,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0
            };
            var output = Path.Combine(Path.GetDirectoryName(
                Environment.GetEnvironmentVariable("MES_REPORT_PATH")) ?? "/reports",
                "pre-submit-reversal-verification.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
            Console.WriteLine("Pre-submit reversal verification complete: " + checks.Count);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = await db.Set<WorkOrder>().Where(x => x.ProductId == product.ProductId)
                .Select(x => x.OrderId).ToArrayAsync();
            var strings = ids.Select(Id).ToArray();
            await db.Set<ProductionEntry>()
                .Where(x => ids.Contains(x.OrderId) && x.Kind == "REVERSAL")
                .ExecuteDeleteAsync();
            await db.Set<ProductionEntry>()
                .Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<CorrectionRequest>()
                .Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<WorkPause>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<WorkCheckItem>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<OrderTransition>()
                .Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<AuditEvent>()
                .Where(x => x.EntityType == "WORK_ORDER" && strings.Contains(x.EntityId!))
                .ExecuteDeleteAsync();
            await db.Set<WorkOrder>().Where(x => ids.Contains(x.OrderId)).ExecuteDeleteAsync();
            await db.Set<Equipment>().Where(x => x.EquipmentId == machine.EquipmentId
                || x.EquipmentId == secondMachine.EquipmentId).ExecuteDeleteAsync();
            await db.Set<Line>().Where(x => x.LineId == line.LineId).ExecuteDeleteAsync();
            await db.Set<Product>()
                .Where(x => x.ProductId == product.ProductId).ExecuteDeleteAsync();
            await db.Set<UserRole>()
                .Where(x => x.UserId == worker.UserId || x.UserId == other.UserId)
                .ExecuteDeleteAsync();
            await db.Set<UserAccount>()
                .Where(x => x.UserId == worker.UserId || x.UserId == other.UserId)
                .ExecuteDeleteAsync();
        }
    }
}

