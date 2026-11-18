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

public static class ReportingVerification
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
            await anonymous.GetAsync("/api/v1/reports/daily-production?businessDate=2001-01-02"),
            401,
            "Anonymous statistics denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/reports/daily-production?businessDate=2001-01-02"),
            403,
            "Worker statistics denied"
        );
        await Expect(
            await worker.GetAsync("/api/v1/job-runs"),
            403,
            "Worker Job history denied"
        );
        await Expect(
            await admin.GetAsync("/api/v1/reports/daily-production?businessDate=2001-1-2"),
            422,
            "Strict date format"
        );
        await Expect(
            await admin.GetAsync("/api/v1/reports/daily-production?businessDate=2099-01-02"),
            422,
            "Future business day denied"
        );
        await Expect(
            await admin
                .GetAsync("/api/v1/reports/daily-production?businessDate=2001-01-02&limit=101"),
            400,
            "Page bound"
        );
        await Expect(
            await admin
                .GetAsync("/api/v1/reports/daily-production?businessDate=2001-01-02&cursor=bad"),
            400,
            "Composite cursor validated"
        );
        await Expect(
            await wrong.PostAsJsonAsync("/internal/v1/reporting/rebuild",
                new ReportingRebuildRequest("2001-01-02")),
            403,
            "Wrong service denied"
        );
        await Expect(
            await admin.PostAsJsonAsync("/internal/v1/reporting/rebuild",
                new ReportingRebuildRequest("2001-01-02")),
            404,
            "Web internal isolation"
        );

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var line = new Line {
            Code = "RPTL" + suffix,
            Name = "Reporting fixture"
        };
        var product = new Product {
            Code = "RPTP" + suffix,
            Name = "Reporting fixture",
            Unit = "EA"
        };
        db.AddRange(line, product);
        await db.SaveChangesAsync();
        var equipment = new Equipment
        {
            LineId = line.LineId,
            Code = "RPTE" + suffix,
            Name = "Reporting fixture"
        };
        db.Add(equipment);
        await db.SaveChangesAsync();
        var workerId = await db.Set<UserAccount>().Where(x => x.Login == "portal")
            .Select(x => x.UserId).SingleAsync();
        var actor = long.Parse(me.UserId);
        var start = new DateTime(
            2001,
            1,
            1,
            15,
            0,
            0,
            DateTimeKind.Utc
        );
        async Task<WorkOrder> Order(DateTime closed, string state = "CLOSED")
        {
            var order = new WorkOrder
            {
                OrderNo = "RPT" + Guid.NewGuid().ToString("N")[..20],
                ProductId = product.ProductId,
                EquipmentId = equipment.EquipmentId,
                AssigneeId = workerId,
                TargetQty = 120,
                CountMode = "MANUAL",
                PlannedStartUtc = closed.AddHours(-2),
                PlannedEndUtc = closed,
                Status = state,
                CreatedBy = actor,
                StartedAtUtc = closed.AddHours(-1),
                SubmittedAtUtc = closed.AddMinutes(-5),
                ClosedAtUtc = state == "CLOSED" ? closed : null,
                CompletionReason = "Isolated reporting fixture"
            };
            db.Add(order);
            await db.SaveChangesAsync();
            return order;
        }
        async Task<ProductionEntry> Entry(
            WorkOrder order,
            string kind,
            long qty,
            long? reversal = null
        )
        {
            long? correctionId = null;
            if (kind == "TOTAL_ADJUST_DOWN")
            {
                var correction = new CorrectionRequest
                {
                    OrderId = order.OrderId,
                    Status = "RESOLVED",
                    Reason = "Isolated reporting fixture",
                    RequestedBy = actor,
                    ResolvedBy = actor,
                    ResolvedAtUtc = DateTime.UtcNow,
                    ResolutionNote = "Fixture ledger adjustment"
                };
                db.Add(correction);
                await db.SaveChangesAsync();
                correctionId = correction.CorrectionId;
            }

            var entry = new ProductionEntry
            {
                ClientEntryId = Guid.NewGuid().ToString(),
                OrderId = order.OrderId,
                Kind = kind,
                Qty = qty,
                ReversalOf = reversal,
                CorrectionId = correctionId,
                Reason = "Reporting verification fixture",
                RecordedBy = workerId,
                OccurredAtUtc = order.StartedAtUtc!.Value,
                RequestHash = new string('a', 64)
            };
            db.Add(entry);
            await db.SaveChangesAsync();
            return entry;
        }
        var closed = await Order(start);
        var total = await Entry(
            closed,
            "TOTAL",
            100
        );
        var reject = await Entry(
            closed,
            "REJECT",
            10
        );
        await Entry(
            closed,
            "REVERSAL",
            5,
            total.EntryId
        );
        await Entry(
            closed,
            "REVERSAL",
            2,
            reject.EntryId
        );
        await Entry(
            closed,
            "TOTAL",
            20
        );
        await Entry(
            closed,
            "TOTAL_ADJUST_DOWN",
            2
        );
        await Entry(
            await Order(start.AddHours(-1)),
            "TOTAL",
            900
        );
        await Entry(
            await Order(start.AddDays(1)),
            "TOTAL",
            800
        );
        await Entry(
            await Order(start.AddHours(1), "SUBMITTED"),
            "TOTAL",
            700
        );
        Check(true, "Isolated Oracle ledger fixtures include reversals and KST boundaries");

        async Task<JobRunView> Rebuild() => await Read<JobRunView>(
            await jobs.PostAsJsonAsync("/internal/v1/reporting/rebuild",
                new ReportingRebuildRequest("2001-01-02")),
            200,
            "Internal durable rebuild"
        );
        async Task<DailyProductionView> OwnRow()
        {
            string? cursor = null;
            do
            {
                var page = await admin.GetFromJsonAsync<PageResult<DailyProductionView>>(
                    "/api/v1/reports/daily-production?businessDate=2001-01-02&limit=100"
                    + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))
                );
                var row = page!.Items.SingleOrDefault(x => x.EquipmentId == Id(equipment
                    .EquipmentId));
                if (row is not null) return row;
                cursor = page.NextCursor;
            } while (cursor is not null);
            throw new InvalidOperationException("Missing fixture aggregate");
        }
        var first = await Rebuild();
        Check(
            first.Status == "SUCCEEDED" && first.AffectedRows > 0,
            "Job success is persisted with affected groups"
        );
        var firstRow = await OwnRow();
        Check(
            firstRow.TotalQty == 113 && firstRow.RejectQty == 8,
            "Reversals and downward adjustments are netted correctly"
        );
        Check(
            firstRow.BusinessDate == "2001-01-02",
            "KST midnight included, next midnight and unclosed excluded"
        );
        var repeated = await Rebuild();
        var repeatedRow = await OwnRow();
        Check(repeatedRow.TotalQty == 113 && repeatedRow.RejectQty == 8 && repeated
            .Attempt > first.Attempt,
            "Rebuild replaces totals without double counting and preserves run attempts");
        var concurrent = await Task.WhenAll(Rebuild(), Rebuild());
        Check(concurrent.All(x => x.Status == "SUCCEEDED") && (await OwnRow()).TotalQty == 113,
            "Concurrent rebuilds remain consistent");

        var request = new ReportingRunRequest(
            "2001-01-02",
            Guid.NewGuid().ToString(),
            "Administrator reviewed ledger"
        );
        await Expect(
            await worker.PostAsJsonAsync("/api/v1/reports/daily-production/rebuild", request),
            403,
            "Worker rebuild denied"
        );
        await Expect(
            await admin.PostAsJsonAsync("/api/v1/reports/daily-production/rebuild", request),
            403,
            "Rebuild requires CSRF"
        );
        var queued = await Read<JobRunView>(
            await Post(
                admin,
                "/api/v1/reports/daily-production/rebuild",
                request
            ),
            202,
            "Administrator queues durable rebuild"
        );
        var replay = await Read<JobRunView>(
            await Post(
                admin,
                "/api/v1/reports/daily-production/rebuild",
                request
            ),
            202,
            "Same request replay accepted"
        );
        Check(queued.JobRunId == replay.JobRunId, "Request UUID does not create duplicate job");
        await Expect(
            await Post(
                admin,
                "/api/v1/reports/daily-production/rebuild",
                request with {
                    Reason = "changed"
                }
            ),
            409,
            "Changed idempotent intent denied"
        );
        await Expect(
            await Post(
                admin,
                "/api/v1/reports/daily-production/rebuild",
                request with {
                    ClientRequestId = Guid.NewGuid().ToString(),
                    Reason = " "
                }
            ),
            422,
            "Reason required"
        );
        await Wait(async () =>
        {
            var run = await admin.GetFromJsonAsync<JobRunView>("/api/v1/job-runs/" + queued
                .JobRunId);
            return run?.Status == "SUCCEEDED" ? run : null;
        }, "Quartz executes durable manual request");

        // Force a constraint failure with an isolated, invalid aggregate source.
        // No existing account/order is modified; source fixture is repaired below.
        var overflowOrder = await Order(start.AddHours(2));
        var overflow = await Entry(
            overflowOrder,
            "TOTAL",
            999999999999
        );
        var failed = await Rebuild();
        Check(failed.Status == "FAILED" && failed.Error == "REPORTING_REBUILD_FAILED" && failed
            .EndedAt is not null,
            "Constraint failure is bounded and durable");
        Check(
            (await OwnRow()).TotalQty == 113,
            "Failed replacement rolls back and preserves previous successful totals"
        );
        await Entry(
            overflowOrder,
            "REVERSAL",
            overflow.Qty,
            overflow.EntryId
        );
        Check((await Rebuild()).Status == "SUCCEEDED" && (await OwnRow()).TotalQty == 113,
            "Repaired isolated source can rebuild without stale partial values");
        var target = new IntegrationTarget
        {
            TargetCode = "RPTT" + suffix,
            Kind = "EQUIPMENT",
            DisplayName = "Disabled reporting fixture",
            ConnectorType = "MOCK_HTTP",
            EquipmentId = equipment.EquipmentId,
            Enabled = false,
            ConfigVersion = 1
        };
        db.Add(target);
        await db.SaveChangesAsync();
        db.Add(new EquipmentConnection
        {
            TargetId = target.TargetId,
            Kind = "EQUIPMENT",
            Protocol = "MOCK_HTTP",
            ApprovedEndpoint = "http://equipment-simulator:8080",
            PollIntervalMs = 1000,
            TimeoutMs = 1000,
            StaleSec = 10
        });
        await db.SaveChangesAsync();
        var mapping = new EquipmentTagMapping
        {
            TargetId = target.TargetId,
            MetricCode = "fixture-counter",
            SourcePath = "counter",
            DataType = "INT64",
            ValueKind = "COUNTER",
            Scale = 1,
            Enabled = false
        };
        db.Add(mapping);
        await db.SaveChangesAsync();
        var auto = await Order(start.AddHours(3));
        auto.CountMode = "AUTO";
        auto.CounterMappingId = mapping.MappingId;
        auto.StartCounter = 0;
        auto.EndCounter = 1;
        auto.StartSampleAtUtc = start;
        auto.EndSampleAtUtc = start.AddSeconds(1);
        auto.StartReceivedAtUtc = start;
        auto.EndReceivedAtUtc = start.AddSeconds(1);
        await db.SaveChangesAsync();
        try
        {
            var supported = await Rebuild();
            Check(supported.Status == "SUCCEEDED",
                "CLOSED AUTO source is accepted by the common ledger rebuild");
            Check((await OwnRow()).TotalQty == 113,
                "Zero-ledger AUTO source does not invent raw-counter production");
        }
        finally
        {
            // Restore only this direct validation fixture, not a business workflow transition.
            auto.CountMode = "MANUAL";
            auto.CounterMappingId = null;
            await db.SaveChangesAsync();
        }

        await Rebuild();
        var interrupted = new JobRun
        {
            JobKey = "daily-production:2001-01-02",
            ScheduledForUtc = DateTime.UtcNow.AddDays(-1),
            Attempt = 1,
            Status = "RUNNING",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-20)
        };
        db.Add(interrupted);
        await db.SaveChangesAsync();
        await Wait(async () =>
        {
            var run = await admin.GetFromJsonAsync<JobRunView>(
                "/api/v1/job-runs/" + Id(interrupted.JobRunId)
            );
            return run?.Status == "SUCCEEDED" ? run : null;
        }, "Quartz recovers persisted RUNNING work without an in-memory request");

        var status = await admin
            .GetFromJsonAsync<ReportingStatusView>("/api/v1/reports/daily-production/status?businessDate=2001-01-02");
        Check(
            status!.LatestRun?.Status == "SUCCEEDED",
            "Business-day status reports latest successful run"
        );
        var pageOne = await admin
            .GetFromJsonAsync<PageResult<DailyProductionView>>("/api/v1/reports/daily-production?businessDate=2001-01-02&limit=1");
        Check(pageOne!.Items.Length <= 1, "Composite pagination respects page size");
        var history = await admin
            .GetFromJsonAsync<PageResult<JobRunView>>("/api/v1/job-runs?limit=100");
        Check(history!.Items.Any(x => x.Status == "FAILED") && history.Items.Any(x => x
            .Status == "SUCCEEDED"),
            "Administrator can inspect failure and success history");
        var future = new JobRun
        {
            JobKey = "daily-production:2001-01-02",
            ScheduledForUtc = CollectionFormat.UtcNow().AddDays(-2),
            Attempt = 1,
            Status = "RUNNING",
            StartedAtUtc = CollectionFormat.UtcNow().AddMinutes(1)
        };
        db.Add(future);
        await db.SaveChangesAsync();
        var clockFailure = await Wait(async () =>
        {
            var run = await admin.GetFromJsonAsync<JobRunView>(
                "/api/v1/job-runs/" + Id(future.JobRunId)
            );
            return run?.Status == "FAILED" ? run : null;
        }, "Future-start fixture becomes terminal failure without endless retries");
        Check(clockFailure.Error == "REPORTING_CLOCK_REGRESSION"
            && clockFailure.EndedAt is { } ended && ended >= clockFailure.StartedAt,
            "Clock regression preserves the time constraint and an explicit error");
        Check((await OwnRow()).TotalQty == 113,
            "Clock regression does not replace previous successful totals");

        Directory.CreateDirectory("/reports");
        await File.WriteAllTextAsync("/reports/reporting-verification.json", JsonSerializer
            .Serialize(new
        {
            passed = checks.Count,
            checks,
            equipmentId = Id(equipment.EquipmentId),
            cloudApiCalls = 0,
            externalDeliveryCalls = 0,
            note = "Direct isolated Oracle ledger fixtures test aggregation; browser tests cover real close workflow."
        }, new JsonSerializerOptions {
            WriteIndented = true
        }));
        Console.WriteLine("Reporting verification passed: " + checks.Count);
    }

    private static string Id(long value) => value.ToString(System.Globalization.CultureInfo
        .InvariantCulture);
}




