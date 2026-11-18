using System.Security.Cryptography;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace FactoryMES.Tooling;
public static class DemoData
{
    public static async Task InitializeAsync(MesDbContext db, string password)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await db.Set<UserAccount>().AnyAsync(x => x.NormalizedLogin == "WORKER.DEMO"))
        {
            var user = Worker("worker.demo", password);
            db.Add(user);
            await db.SaveChangesAsync();
            db.Add(
                new UserRole {
                    UserId = user.UserId,
                    Role = "WORKER"
                }
            );
        }
        if (!await db.Set<Product>().AnyAsync(x => x.Code == "DEMO-EA"))
        {
            db.Add(
                new Product {
                    Code = "DEMO-EA",
                    Name = "시연 부품",
                    Unit = "EA"
                }
            );
        }
        var line = await db.Set<Line>().SingleOrDefaultAsync(x => x.Code == "DEMO-LINE");
        if (line is null)
        {
            line = new Line
            {
                Code = "DEMO-LINE",
                Name = "시연 생산라인"
            };
            db.Add(line);
            await db.SaveChangesAsync();
        }
        if (!await db.Set<Equipment>().AnyAsync(x => x.Code == "DEMO-MANUAL"))
        {
            db.Add(
                new Equipment {
                    LineId = line.LineId,
                    Code = "DEMO-MANUAL",
                    Name = "시연 수동 조립 설비"
                }
            );
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        Console.WriteLine("Demo master data ready. Existing records and passwords retained.");
        Console.WriteLine("Worker login: worker.demo; password: ignored .secrets/worker_password.");
    }

    public static UserAccount Worker(string login, string password)
    {
        var user = new UserAccount
        {
            Login = login,
            NormalizedLogin = login.ToUpperInvariant(),
            Name = login,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, password);
        return user;
    }

    public static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
