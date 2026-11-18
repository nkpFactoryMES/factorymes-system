using FactoryMES.Tooling;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FactoryMES.Foundation;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oracle.EntityFrameworkCore.Infrastructure;

if (args.Length == 0)
    throw new ArgumentException("Use init-secrets <directory>, init-admin or verify.");
var mode = args[0];
if (mode == "init-secrets")
{
    var path = Path.GetFullPath(args.Length > 1 ? args[1] : ".secrets");
    Directory.CreateDirectory(path);
    string ReadOrCreate(string name, Func<string> create)
    {
        var file = Path.Combine(path, name);
        if (!File.Exists(file))
            File.WriteAllText(file, create());
        return File.ReadAllText(file)
            .Trim();
    }

    string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var rootPassword = ReadOrCreate("oracle_root_password", RandomSecret);
    var appPassword = ReadOrCreate("oracle_app_password", RandomSecret);
    ReadOrCreate("admin_password", RandomSecret);
    ReadOrCreate("worker_password", RandomSecret);
    ReadOrCreate("portal_password", RandomSecret);
    ReadOrCreate(
        "oracle_connection",
        () => $"User Id=factorymes;Password={appPassword};Data Source=oracle:1521/FREEPDB1;Pooling=true;Connection Timeout=5;"
    );
    ReadOrCreate(
        "oracle_connection_host",
        () => $"User Id=factorymes;Password={appPassword};Data Source=localhost:1527/FREEPDB1;Pooling=true;Connection Timeout=5;"
    );
    ReadOrCreate(
        "oracle_bootstrap_sql",
        () => $"SET ECHO OFF\nSET VERIFY OFF\nCONNECT factorymes/\"{appPassword}\"@//localhost:1521/FREEPDB1\n@/opt/factorymes/schema/01_tables.sql\n@/opt/factorymes/schema/02_indexes.sql\n@/opt/factorymes/schema/05_notification_settings_upgrade.sql\nEXIT\n"
    );
    foreach (var prefix in new[]
    {
        "service",
        "collection"
    }

    )
    {
        var privateFile = Path.Combine(path, prefix + "_private_key");
        var publicFile = Path.Combine(path, prefix + "_public_key");
        if (!File.Exists(privateFile))
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(privateFile, rsa.ExportRSAPrivateKeyPem());
            File.WriteAllText(publicFile, rsa.ExportSubjectPublicKeyInfoPem());
        }
        else if (!File.Exists(publicFile))
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(privateFile));
            File.WriteAllText(publicFile, rsa.ExportSubjectPublicKeyInfoPem());
        }
    }

    var clients = new Dictionary<string, string>();
    foreach (var id in new[]
    {
        "integration-gateway",
        "equipment-connector",
        "ai-connector",
        "jobs-worker",
        "verification"
    }

    )
        clients[id] = ReadOrCreate(id + "_client_secret", RandomSecret);
    ReadOrCreate("service_clients", () => JsonSerializer.Serialize(clients));
    var tlsPassword = ReadOrCreate("https_password", RandomSecret);
    if (!File.Exists(Path.Combine(path, "https_pfx")))
    {
        using var key = RSA.Create(2048);
        var request =
            new CertificateRequest(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("web");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                false,
                false,
                0,
                false
            )
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false
            )
        );
        var usages = new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var cert =
            request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(90)
        );
        File.WriteAllBytes(
            Path.Combine(path, "https_pfx"),
            cert.Export(X509ContentType.Pfx, tlsPassword)
        );
        File.WriteAllText(Path.Combine(path, "https_public_cert"), cert.ExportCertificatePem());
    }

    Console.WriteLine(
        "Local secrets initialized; values were not printed. Existing files were retained."
    );
    return;
}

if (mode == "verify-modbus-protocol")
{
    await ModbusProtocolVerification.RunAsync();
    return;
}

string ReadSecret(string name) =>
    File.ReadAllText(
    Path.Combine(Environment.GetEnvironmentVariable("MES_SECRET_DIRECTORY")
    ?? "/run/secrets", name)
)
        .Trim();
var connection =
    mode == "verify-model"
    ? "User Id=unused;Password=unused;Data Source=localhost:1527/FREEPDB1;"
    : ReadSecret(
    Environment.GetEnvironmentVariable("MES_CONNECTION_SECRET")
    ?? "oracle_connection"
);
var options =
    new DbContextOptionsBuilder<MesDbContext>().UseOracle(
    connection,
    oracle => oracle.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion23)
)
        .Options;
await using var db = new MesDbContext(options);
if (mode == "verify-modbus-collection")
{
    await ModbusCollectionVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-auto-production")
{
    await AutoProductionVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "upgrade-notification-settings")
{
    await NotificationSettingsSchema.ApplyAsync(db);
    Console.WriteLine("Notification settings additive upgrade completed; existing rows retained.");
    return;
}

if (mode == "upgrade-auto-production")
{
    await AutoProductionSchema.ApplyAsync(db);
    Console.WriteLine("AUTO production upgrade completed; existing data retained.");
    return;
}

if (mode == "upgrade-ai-retry")
{
    await db.Database.ExecuteSqlRawAsync("""
        DECLARE
            n NUMBER;
        BEGIN
            SELECT COUNT(*) INTO n FROM USER_TAB_COLUMNS
            WHERE TABLE_NAME = 'INTEGRATION_JOB' AND COLUMN_NAME = 'RETRY_AT_UTC';
            IF n = 0 THEN
                EXECUTE IMMEDIATE 'ALTER TABLE INTEGRATION_JOB ADD RETRY_AT_UTC TIMESTAMP(6)';
            END IF;
        END;
        """);
    Console.WriteLine("AI retry additive schema upgrade completed; existing rows retained.");
    return;
}

if (mode == "verify-host-db")
{
    var hostConnection = new OracleConnectionStringBuilder(connection)
    {
        DataSource = "127.0.0.1:1527/FREEPDB1",
        Pooling = false
    };
    await using var hostDb = new OracleConnection(hostConnection.ConnectionString);
    await hostDb.OpenAsync();
    await using var command = hostDb.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM USER_TABLES";
    Console.WriteLine("Host Oracle login OK, tables: " + await command.ExecuteScalarAsync());
    return;
}

if (mode == "verify-model")
{
    if (db.Model.GetEntityTypes()
        .Count() != 32
        || db.Model.GetEntityTypes()
            .SelectMany(x => x.GetForeignKeys())
            .Count() != 56)
        throw new InvalidOperationException("Model does not match the 32-table/56-FK baseline.");
    Console.WriteLine(
        "Model validation passed: 32 entities, 56 foreign keys; no DB connection made."
    );
    return;
}

if (mode == "init-admin")
{
    if (await db.Set<UserAccount>()
        .AnyAsync(x => x.NormalizedLogin == "ADMIN"))
    {
        Console.WriteLine("Existing admin retained; password was not reset.");
        return;
    }

    var user = new UserAccount
    {
        Login = "admin",
        NormalizedLogin = "ADMIN",
        Name = "Local Administrator",
        Active = true,
        SecurityStamp = Guid.NewGuid()
            .ToString("N")
    };
    user.PasswordHash =
        new PasswordHasher<UserAccount>().HashPassword(
        user,
        ReadSecret("admin_password")
    );
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Add(user);
    await db.SaveChangesAsync();
    db.Add(new UserRole
    {
        UserId = user.UserId,
        Role = "ADMIN"
    });
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    Console.WriteLine(
        "Local admin created. Password is in the ignored admin_password secret file; it was not printed."
    );
    return;
}

if (mode == "init-dev-accounts")
{
    await DevelopmentAccounts.ConfigureAsync(db, ReadSecret);
    return;
}

if (mode == "init-demo")
{
    await DemoData.InitializeAsync(db, ReadSecret("worker_password"));
    return;
}

if (mode == "verify-outbox-retry")
{
    await OutboxRetryVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analysis-input-retry")
{
    await AnalysisInputRetryVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analysis-recovery")
{
    await AnalysisRecoveryVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analysis-rotation-replay")
{
    await ScheduleRotationVerification.ReplayAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analysis-rotation")
{
    await ScheduleRotationVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analysis-aggregation")
{
    await AnalysisAggregationVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-local-ai")
{
    await LocalAiVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-analyses")
{
    await AnalysisVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-reporting")
{
    await ReportingVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-delivery-adapters")
{
    await LocalDeliveryVerification.RunAsync(db, ReadSecret);
    return;
}
if (mode == "verify-deliveries")
{
    await DeliveryVerification.RunAsync(db, ReadSecret);
    return;
}
if (mode == "verify-high-alarms")
{
    await HighAlarmVerification.RunAsync(db, ReadSecret);
    return;
}
if (mode == "verify-alarm-actions")
{
    await AlarmActionVerification.RunAsync(db, ReadSecret);
    return;
}
if (mode == "verify-alarms")
{
    await AlarmVerification.RunAsync(db, ReadSecret);
    return;
}
if (mode == "verify-collection")
{
    await CollectionVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-pre-submit-reversal")
{
    await PreSubmitReversalVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-lifecycle")
{
    await LifecycleVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode == "verify-orders")
{
    await OrderVerification.RunAsync(db, ReadSecret);
    return;
}

if (mode != "verify")
    throw new ArgumentException("Unknown mode.");
var passed = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
    passed.Add(name);
    Console.WriteLine("PASS " + name);
}

Check(db.Model.GetEntityTypes()
    .Count() == 32, "32 mapped entities");
Check(
    db.Model.GetEntityTypes()
        .SelectMany(x => x.GetForeignKeys())
        .Count() == 56,
    "56 mapped foreign keys"
);
var now =
    DateTime.SpecifyKind(
    new DateTime(
        2026,
        11,
        12,
        14,
        0,
        0
    ),
    DateTimeKind.Utc
)
        .AddTicks(1234560);
var converted = (DateTime)UtcDateTime.Converter.ConvertFromProvider(now)!;
Check(converted.Kind == DateTimeKind.Utc, "UTC read kind");
bool rejected = false;
try
{
    UtcDateTime.RequireUtc(DateTime.SpecifyKind(now, DateTimeKind.Unspecified));
}
catch (ArgumentException)
{
    rejected = true;
}

Check(rejected, "Unspecified timestamp rejected");
rejected = false;
try
{
    CostPolicy.EnsureLocalOnly(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
    {
        "ExecutionPolicy:CloudAiEnabled",
        "true"
    }
    })
            .Build()
    );
}
catch (InvalidOperationException)
{
    rejected = true;
}

Check(rejected, "Cloud activation rejected");
rejected = false;
try
{
    CostPolicy.EnsureLocalOnly(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
    {
        "ExecutionPolicy:ExternalDeliveryEnabled",
        "true"
    }
    })
            .Build()
    );
}
catch (InvalidOperationException)
{
    rejected = true;
}

Check(rejected, "External delivery activation rejected");
// Verify every entity can be queried against the approved physical table without changing data.
var queryMethod = typeof(QueryProbe).GetMethod(nameof(QueryProbe.ReadOne))!;
foreach (var type in db.Model.GetEntityTypes())
    await (Task)queryMethod.MakeGenericMethod(type.ClrType)
        .Invoke(null, [
            db
        ])!;
Check(true, "32 entity queries against Oracle");
await using (var oracle = new OracleConnection(connection))
{
    await oracle.OpenAsync();
    await using var command = oracle.CreateCommand();
    command.BindByName = true;
    command.CommandText =
        "SELECT CAST(:stamp AS TIMESTAMP(6)), CAST(:amount AS NUMBER(18,6)), CAST(:id AS NUMBER(19,0)), CAST(:day AS DATE) FROM DUAL";
    command.Parameters.Add("stamp", OracleDbType.TimeStamp)
        .Value = now;
    command.Parameters.Add("amount", OracleDbType.Decimal)
        .Value = 123456789012.123456m;
    command.Parameters.Add("id", OracleDbType.Decimal)
        .Value = (decimal)long.MaxValue;
    command.Parameters.Add("day", OracleDbType.Date)
        .Value = new DateTime(
            2026,
            11,
            12
        );
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    Check(reader.GetDateTime(0)
        .Ticks == now.Ticks, "Oracle TIMESTAMP(6) ticks roundtrip");
    Check(reader.GetDecimal(1) == 123456789012.123456m, "Oracle NUMBER(18,6) precision");
    Check(reader.GetDecimal(2) == long.MaxValue, "Oracle NUMBER(19,0) Int64 boundary");
    Check(reader.GetDateTime(3) == new DateTime(2026, 11, 12), "Oracle DATE business day");
}

// Collection proof tests use ephemeral local keys, never a paid endpoint.
using (var rsa = RSA.Create(2048))
{
    var t = DateTimeOffset.UtcNow;
    var nbf = DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());
    var claims = new[]
    {
        new Claim("target_id", "FILLER-01"),
        new Claim("instance_id", "equipment-connector-1"),
        new Claim("config_version", "1"),
        new Claim("config_hash", new string ('a', 64)),
        new Claim("scope", "telemetry:collect"),
        new Claim(
            "iat",
            nbf.ToUnixTimeSeconds()
                .ToString(),
            ClaimValueTypes.Integer64
        )
    };
    var token =
        new JwtSecurityToken(
            "mes-backend",
            "mes-equipment-collection",
            claims,
            nbf.UtcDateTime,
            nbf.AddSeconds(15)
                .UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        );
    var encoded = new JwtSecurityTokenHandler().WriteToken(token);
    var context =
        new MeasurementContext(
            "FILLER-01",
            "equipment-connector-1",
            1,
            new string ('a', 64),
            nbf.AddSeconds(10),
            nbf.AddSeconds(50)
        );
    CollectionProof.Validate(
        encoded,
        rsa,
        context
    );
    Check(true, "Expired proof permits measurement-time valid delayed sample");
    foreach (var item in new[]
    {
        ("wrong target", context with
        {
            TargetId = "OTHER"
        }),
        ("new measurement after expiration", context with
        {
            MeasuredAt = nbf.AddSeconds(16)
        }),
        ("past receive grace", context with
        {
            ReceivedAt = nbf.AddSeconds(76)
        }),
        ("wrong configuration hash", context with
        {
            ConfigHash = new string ('b', 64)
        })
    }

    )
    {
        rejected = false;
        try
        {
            CollectionProof.Validate(
                encoded,
                rsa,
                item.Item2
            );
        }
        catch (SecurityTokenException)
        {
            rejected = true;
        }

        Check(rejected, "Collection rejects " + item.Item1);
    }
}

var web = new Uri(Environment.GetEnvironmentVariable("MES_WEB_URL") ?? "https://web:8443");
using var pinned =
    X509Certificate2.CreateFromPem(
    File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("MES_SECRET_DIRECTORY")
    ?? "/run/secrets", "https_public_cert"))
);
using var handler = new HttpClientHandler
{
    CookieContainer = new CookieContainer(),
    ServerCertificateCustomValidationCallback =
        (
            r,
            cert,
            chain,
            errors
        ) => cert?.Thumbprint == pinned.Thumbprint
};
using var client = new HttpClient(handler)
{
    BaseAddress = web,
    Timeout = TimeSpan.FromSeconds(10)
};
using (var response = await client.GetAsync("/"))
    Check(response.IsSuccessStatusCode, "HTTPS Blazor host");
using (var response = await client.GetAsync("/internal/v1/status"))
    Check(response.StatusCode == HttpStatusCode.NotFound, "Web does not forward internal routes");
using (var response = await client.GetAsync("/api/v1/auth/me"))
    Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous user denied");
using (var response =
    await client.PostAsJsonAsync(
    "/api/v1/auth/login",
    new
    {
        login = "admin",
        password = "invalid-password"
    }
))
    Check(response.StatusCode == HttpStatusCode.Forbidden, "Login requires CSRF");
async Task Csrf()
{
    var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
    client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
    client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("requestToken")
        .GetString());
}

await Csrf();
using (var response =
    await client.PostAsJsonAsync(
    "/api/v1/auth/login",
    new
    {
        login = "admin",
        password = ReadSecret("admin_password")
    }
))
    Check(response.IsSuccessStatusCode, "Oracle-backed admin cookie login");
using (var response = await client.GetAsync("/api/v1/execution-policy"))
{
    Check(response.IsSuccessStatusCode, "ADMIN authorization");
    var policy = await response.Content.ReadFromJsonAsync<JsonElement>();
    Check(
        !policy.GetProperty("cloudAiEnabled")
            .GetBoolean()
        && !policy.GetProperty("externalDeliveryEnabled")
            .GetBoolean(),
        "Runtime paid-provider gates disabled"
    );
}

await Csrf();
using (var response = await client.PostAsync("/api/v1/auth/logout", null))
    Check(response.StatusCode == HttpStatusCode.NoContent, "Authenticated CSRF logout");
using (var response = await client.GetAsync("/api/v1/auth/me"))
    Check(response.StatusCode == HttpStatusCode.Unauthorized, "Logout removes session");
// Own temporary fixture only; production and existing accounts are never deleted.
var workerName = "verify-" + Guid.NewGuid()
    .ToString("N");
var workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
var worker = new UserAccount
{
    Login = workerName,
    NormalizedLogin = workerName.ToUpperInvariant(),
    Name = "Verification worker",
    Active = true,
    SecurityStamp = Guid.NewGuid()
        .ToString("N")
};
worker.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(worker, workerPassword);
db.Add(worker);
await db.SaveChangesAsync();
db.Add(new UserRole
{
    UserId = worker.UserId,
    Role = "WORKER"
});
await db.SaveChangesAsync();
try
{
    using var workerHandler = new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        ServerCertificateCustomValidationCallback =
            (
                r,
                cert,
                chain,
                errors
            ) => cert?.Thumbprint == pinned.Thumbprint
    };
    using var workerClient = new HttpClient(workerHandler)
    {
        BaseAddress = web,
        Timeout = TimeSpan.FromSeconds(10)
    };
    var csrf = await workerClient.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
    workerClient.DefaultRequestHeaders.Add(
        "X-CSRF-TOKEN",
        csrf.GetProperty("requestToken")
            .GetString()
    );
    using (var response =
        await workerClient.PostAsJsonAsync(
        "/api/v1/auth/login",
        new
        {
            login = workerName,
            password = workerPassword
        }
    ))
        Check(response.IsSuccessStatusCode, "WORKER cookie login");
    using (var response = await workerClient.GetAsync("/api/v1/execution-policy"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "WORKER denied ADMIN endpoint");
    await db.Entry(worker)
        .ReloadAsync();
    worker.Active = false;
    worker.SecurityStamp = Guid.NewGuid()
        .ToString("N");
    await db.SaveChangesAsync();
    using (var response = await workerClient.GetAsync("/api/v1/auth/me"))
        Check(
            response.StatusCode == HttpStatusCode.Unauthorized,
            "Account disable invalidates existing cookie"
        );
}
finally
{
    db.RemoveRange(await db.Set<UserRole>()
        .Where(x => x.UserId == worker.UserId)
        .ToListAsync());
    await db.SaveChangesAsync();
    await db.Entry(worker)
        .ReloadAsync();
    db.Remove(worker);
    await db.SaveChangesAsync();
}

using var internalClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10)
};
var backend = Environment.GetEnvironmentVariable("MES_BACKEND_URL") ?? "http://mes-backend:8080";
using (var response = await internalClient.GetAsync(backend + "/internal/v1/status"))
    Check(
        response.StatusCode == HttpStatusCode.Unauthorized,
        "Internal API rejects missing service token"
    );
string? backendToken = null;
foreach (var audience in new[]
{
    "mes-backend",
    "integration-gateway",
    "equipment-connector",
    "ai-connector",
    "jobs-worker"
}

)
{
    using var issued =
        await internalClient.PostAsJsonAsync(
        backend + "/internal/v1/auth/token",
        new
        {
            clientId = "verification",
            clientSecret = ReadSecret("verification_client_secret"),
            audience
        }
    );
    issued.EnsureSuccessStatusCode();
    var payload = await issued.Content.ReadFromJsonAsync<JsonElement>();
    var token = payload.GetProperty("accessToken")
        .GetString();
    using var request =
        new HttpRequestMessage(
        HttpMethod.Get,
        $"http://{audience}:8080/internal/v1/status"
    );
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await internalClient.SendAsync(request);
    Check(response.IsSuccessStatusCode, "Service JWT audience " + audience);
    if (audience == "mes-backend")
        backendToken = token;
}

using (var wrong =
    new HttpRequestMessage(
    HttpMethod.Get,
    "http://integration-gateway:8080/internal/v1/status"
))
{
    wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backendToken);
    using var response = await internalClient.SendAsync(wrong);
    Check(response.StatusCode == HttpStatusCode.Unauthorized, "Service JWT wrong audience denied");
}

var result = new
{
    passed = passed.Count,
    checks = passed,
    cloudApiCalls = 0,
    externalDeliveryCalls = 0,
    scope = "scaffold/auth/mapping only; business APIs are not yet implemented"
};
Console.WriteLine(JsonSerializer.Serialize(result));
var reportPath = Environment.GetEnvironmentVariable("MES_REPORT_PATH");
if (reportPath is not null)
    await File.WriteAllTextAsync(
        reportPath,
        JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented =
        true
    })
    );

public static class QueryProbe
{
    public static async Task ReadOne<T>(MesDbContext db)
        where T : class => await db.Set<T>()
            .AsNoTracking()
            .Take(1)
            .ToListAsync();
}


