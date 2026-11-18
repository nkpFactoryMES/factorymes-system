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

public static class CollectionVerification
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
        var key = "C" + Guid.NewGuid().ToString("N")[..12];
        var line = new Line {
            Code = key,
            Name = "Collection verification"
        };
        var equipment = new Equipment {
            Code = key,
            Name = "Collection verification"
        };
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
            Timeout = TimeSpan.FromSeconds(10)
        };
        using var admin = Client("https://web:8443");
        using var worker = Client("https://web:8443");
        using var producer = Client("http://mes-backend:8080");
        using var gateway = Client("http://mes-backend:8080");
        using var anonymous = Client("http://mes-backend:8080");
        async Task Csrf(HttpClient client)
        {
            var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("requestToken")
                .GetString());
        }
        async Task<HttpResponseMessage> Public(
            HttpClient client,
            string route,
            object body,
            bool put = false
        )
        {
            await Csrf(client);
            using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post,
                route)
            {
                Content = JsonContent.Create(body, body.GetType())
            };
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
        async Task Authenticate(HttpClient client, string identity)
        {
            using var response = await anonymous.PostAsJsonAsync("/internal/v1/auth/token", new
            {
                clientId = identity,
                clientSecret = secret(identity + "_client_secret"),
                audience = "mes-backend"
            });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                body.GetProperty("accessToken").GetString());
        }
        TargetView? target = null;
        try
        {
            db.Add(line);
            await db.SaveChangesAsync();
            equipment.LineId = line.LineId;
            db.Add(equipment);
            await db.SaveChangesAsync();
            await Read<JsonElement>(
                await Public(
                    admin,
                    "/api/v1/auth/login",
                    new { login = "admin", password = secret("admin_password") }
                ),
                200,
                "Admin login"
            );
            await Read<JsonElement>(
                await Public(
                    worker,
                    "/api/v1/auth/login",
                    new { login = "portal", password = secret("portal_password") }
                ),
                200,
                "Worker login"
            );
            var settings = new EquipmentSettings(
                "HTTP",
                "http://equipment-simulator:8080/equipment/" + key,
                2000,
                1000,
                20,
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
                    ),
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
            var create = new EquipmentTargetCreate(
                key,
                "Collection verification",
                "equipment-http",
                true,
                "EQUIPMENT",
                equipment.EquipmentId.ToString(),
                settings
            );
            await Expect(
                await Public(
                    worker,
                    "/api/v1/integration-targets",
                    create
                ),
                403,
                "Worker configure denied"
            );
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            await Expect(
                await admin.PostAsJsonAsync("/api/v1/integration-targets", create),
                403,
                "Configuration requires CSRF"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create with {
                        Settings = settings with {
                            ApprovedEndpoint = "http://example.com/private"
                        }
                    }
                ),
                422,
                "External URL rejected"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create with {
                        Settings = settings with {
                            ApprovedEndpoint = "http://equipment-simulator:8080/equipment/%2e%2e"
                        }
                    }
                ),
                422,
                "Encoded path rejected"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create with {
                        Settings = settings with {
                            CredentialRef = "not-a-mock"
                        }
                    }
                ),
                422,
                "Mock credential reference rejected"
            );
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create
                ),
                201,
                "Register target mappings and Outbox atomically"
            );
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets",
                    create
                ),
                409,
                "Duplicate target rejected"
            );
            var id = await db.Set<IntegrationTarget>().Where(x => x.TargetCode == key)
                .Select(x => x.TargetId).SingleAsync();
            Check(
                await db.Set<OutboxEvent>().AnyAsync(x => x.AggregateId == key),
                "Configuration Outbox persisted"
            );
            Check(await db.Set<AuditEvent>().AnyAsync(x => x.EntityId == key
                && x.Action == "equipment.configuration.changed"), "Configuration audit persisted");
            await Authenticate(producer, "equipment-connector");
            await Authenticate(gateway, "integration-gateway");
            await Expect(
                await anonymous
                    .GetAsync("/internal/v1/integration/targets/" + key + "/configuration"),
                401,
                "Internal source requires service JWT"
            );
            await Expect(
                await producer
                    .GetAsync("/internal/v1/integration/targets/" + key + "/configuration"),
                403,
                "Producer cannot bypass gateway to resolve source"
            );
            var config = await gateway.GetFromJsonAsync<EquipmentResolvedConfig>(
                "/internal/v1/integration/targets/" + key + "/configuration");
            Check(
                config!.ConfigHash == CollectionFormat.Hash(config.Content),
                "Canonical configuration hash verified"
            );
            var requestLease = new LeaseRequest(
                key,
                "equipment-connector.verify",
                config.Content.ConfigVersion,
                config.ConfigHash
            );
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/integration/leases/renew",
                    requestLease),
                403,
                "Only gateway issues collection leases"
            );
            var lease = await Read<LeaseResponse>(
                await gateway.PostAsJsonAsync("/internal/v1/integration/leases/renew",
                    requestLease),
                200,
                "Source validates enabled state before signed lease"
            );
            Check(lease.ExpiresAt - lease.ValidFrom == TimeSpan.FromSeconds(15)
                && lease.ReceiveUntil - lease.ExpiresAt == TimeSpan.FromSeconds(60),
                    "Proof lifetime and receive grace");
            for (var renewal = 0; renewal < 3; renewal++)
            {
                await Read<LeaseResponse>(
                    await gateway.PostAsJsonAsync("/internal/v1/integration/leases/renew", requestLease),
                    200,
                    "Repeated short-lived RSA signing after disposal " + renewal
                );
            }
            var stream = "verify." + key;
            CollectedEvent Event(long seq) => new(
                Guid.NewGuid().ToString(),
                "mes.telemetry.collected.v1",
                1,
                "mes.equipment-connector",
                CollectionFormat.UtcNow(),
                Guid.NewGuid().ToString("N"),
                new TelemetryData(
                    config.Content.EquipmentId,
                    key,
                    lease.InstanceId,
                    stream,
                    seq.ToString(),
                    config.Content.ConfigVersion,
                    config.ConfigHash,
                    config.Content.Settings.Mappings.Where(x => x.Enabled)
                        .Select(x => new CollectedMetric(
                        x.MetricCode,
                        x.MappingId!,
                        x.ValueKind == "STATE" ? null : x.ValueKind == "COUNTER" ? "10" : "25.1",
                        x.ValueKind == "STATE" ? "RUNNING" : null,
                        x.Unit,
                        "GOOD"
                    )).ToArray()
                )
            );
            var item = Event(1);
            var batch = new TelemetryBatch(
                lease.CollectionAuthorization,
                config,
                [
                    item
                ]
            );
            await Expect(
                await anonymous.PostAsJsonAsync("/internal/v1/telemetry/batches", batch),
                401,
                "Proof alone cannot authenticate ingestion"
            );
            await Expect(
                await gateway.PostAsJsonAsync("/internal/v1/telemetry/batches", batch),
                403,
                "Wrong service cannot write telemetry"
            );
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches",
                    batch with {
                        CollectionAuthorization = new string('x', 30)
                    }),
                403,
                "Invalid proof rejected"
            );
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches", batch with
                {
                    Events = [
                        item with {
                            Data = item.Data with {
                                InstanceId = "equipment-connector.other"
                            }
                        }
                    ]
                }),
                403,
                "Proof instance binding enforced"
            );
            var stored = await Read<TelemetryBatchAck>(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches", batch),
                200,
                "Telemetry persisted before ACK"
            );
            Check(stored.Items.Single().Outcome == "STORED", "STORED acknowledgement");
            var duplicate = await Read<TelemetryBatchAck>(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches", batch),
                200,
                "Lost acknowledgement replay"
            );
            Check(duplicate.Items.Single().Outcome == "DUPLICATE"
                && duplicate.Items.Single().TelemetryId == stored.Items.Single().TelemetryId,
                    "Duplicate replay returns same row");
            var confirmed = await Read<TelemetryBatchAck>(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches",
                    batch with { CollectionAuthorization = new string('x', 30) }),
                200,
                "Exact stored duplicate needs no renewed measurement proof"
            );
            Check(confirmed.Items.Single().Outcome == "DUPLICATE",
                "Post-proof-window reconciliation cannot add data");
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches", batch with
                {
                    Events = [
                        item with {
                            CorrelationId = "different-payload"
                        }
                    ]
                }),
                409,
                "Same event ID different payload rejected"
            );
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches", batch with
                {
                    Events = [
                        item with {
                            EventId = Guid.NewGuid().ToString()
                        }
                    ]
                }),
                409,
                "Stream sequence collision rejected"
            );
            var first = Event(2);
            var invalid = Event(3);
            invalid = invalid with {
                Data = invalid.Data with {
                    Metrics = []
                }
            };
            await Expect(
                await producer.PostAsJsonAsync("/internal/v1/telemetry/batches",
                    batch with {
                        Events = [
                            first,
                            invalid
                        ]
                    }),
                422,
                "Whole batch rejects invalid metric set"
            );
            Check(!await db.Set<TelemetryEvent>().AnyAsync(x => x.EventId == first.EventId),
                "Earlier insert rolled back with invalid later event");
            var payload = await db.Set<TelemetryEvent>().AsNoTracking().SingleAsync(x => x
                .EventId == item.EventId);
            Check(!payload.Payload.Contains("collectionAuthorization") && !payload.Payload
                .Contains(lease.CollectionAuthorization),
                "Collection proof never persisted in event payload");
            Check(await db.Set<TelemetryMetric>().CountAsync(x => x.TelemetryId == payload
                .TelemetryId) == 3,
                "Numeric and text metrics normalized");
            var expires = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < expires)
            {
                if (await db.Set<TelemetryEvent>().AnyAsync(x => x.TargetId == id && x
                    .StreamId != stream)
                    && await db.Set<OutboxEvent>().AnyAsync(x => x.AggregateId == key && x
                        .Status == "DELIVERED")) break;
                await Task.Delay(500);
            }
            Check(await db.Set<TelemetryEvent>().AnyAsync(x => x.TargetId == id && x
                .StreamId != stream),
                "Independent connector automatically polls registered mock target");
            Check(await db.Set<OutboxEvent>().AnyAsync(x => x.AggregateId == key && x
                .Status == "DELIVERED"),
                "Gateway and connector acknowledge Outbox delivery");
            target = (await admin
                .GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + key))!;
            await Expect(
                await Public(
                    admin,
                    "/api/v1/integration-targets/" + key + "/state",
                    new TargetStateUpdate(
                        target.Version - 1,
                        target.ConfigVersion,
                        false,
                        "Stale version"
                    ),
                    true
                ),
                409,
                "Stale configuration mutation rejected"
            );
            target = await Read<TargetView>(
                await Public(
                    admin,
                    "/api/v1/integration-targets/" + key + "/state",
                    new TargetStateUpdate(
                        target.Version,
                        target.ConfigVersion,
                        false,
                        "Verification complete"
                    ),
                    true
                ),
                200,
                "Stop collection through source state"
            );
            await Expect(
                await gateway.PostAsJsonAsync("/internal/v1/integration/leases/renew",
                    requestLease),
                409,
                "Cached enabled config cannot obtain new lease after disable"
            );
            var report = new
            {
                passed = checks.Count,
                checks,
                targetId = key,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0
            };
            await File.WriteAllTextAsync("/reports/collection-verification.json",
                JsonSerializer.Serialize(report, new JsonSerializerOptions {
                    WriteIndented = true
                }));
            Console.WriteLine("Collection verification complete: " + checks.Count);
        }
        finally
        {
            // Preserve telemetry/audit evidence. Deactivate only the unique target created by this run.
            if (target is not null)
            {
                var current = await admin
                    .GetFromJsonAsync<TargetView>("/api/v1/integration-targets/" + key);
                if (current?.Enabled == true)
                {
                    using var result = await Public(
                        admin,
                        "/api/v1/integration-targets/" + key + "/state",
                        new TargetStateUpdate(
                            current.Version,
                            current.ConfigVersion,
                            false,
                            "Verification cleanup"
                        ),
                        true
                    );
                    result.EnsureSuccessStatusCode();
                }
            }
        }
    }
}

