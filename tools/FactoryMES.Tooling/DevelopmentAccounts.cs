using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

public static class DevelopmentAccounts
{
    public static async Task ConfigureAsync(MesDbContext db, Func<string, string> secret)
    {
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
        {
            throw new InvalidOperationException("Development account setup requires Development mode.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        await ResetAsync(
            db,
            "admin",
            "ADMIN",
            secret("admin_password")
        );
        await ResetAsync(
            db,
            "portal",
            "WORKER",
            secret("portal_password")
        );
        await transaction.CommitAsync();
        Console.WriteLine("Development admin and portal accounts configured; prior sessions invalidated.");
    }

    private static async Task ResetAsync(
        MesDbContext db,
        string login,
        string role,
        string password
    )
    {
        if (password.Length is < 1 or > 256)
        {
            throw new InvalidOperationException("Development password file is empty or too long.");
        }

        var normalized = login.ToUpperInvariant();
        var users = await db.Set<UserAccount>()
            .FromSqlInterpolated(
                $"SELECT * FROM USER_ACCOUNT WHERE NORMALIZED_LOGIN = {normalized} FOR UPDATE"
            )
            .ToListAsync();
        var user = users.SingleOrDefault();
        if (user is null)
        {
            user = DemoData.Worker(login, password);
            db.Add(user);
        }

        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, password);
        user.Active = true;
        user.FailedCount = 0;
        user.LockUntilUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();

        var roles = await db.Set<UserRole>()
            .Where(x => x.UserId == user.UserId)
            .ToListAsync();
        db.RemoveRange(roles.Where(x => x.Role != role));
        if (!roles.Any(x => x.Role == role))
        {
            db.Add(new UserRole
            {
                UserId = user.UserId,
                Role = role
            });
        }

        db.Add(new AuditEvent
        {
            Principal = "LOCAL_TOOLING",
            Action = "development.credentials.reset",
            EntityType = "USER_ACCOUNT",
            EntityId = user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Detail = JsonSerializer.Serialize(new
            {
                login,
                role
            }),
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
