using Bistro.Orders;
using Bistro.Sittings;
using Bistro.Confirmations;
using Bistro.Shared.Auth;
using Bistro.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bistro.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("BISTRO_TEST_DB")
        ?? "Host=localhost;Port=5483;Database=bistro_test;Username=bistro;Password=bistro";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static SittingsDbContext Sittings() => new(Options<SittingsDbContext>(SittingsDbContext.SchemaName));
    public static ConfirmationsDbContext Confirmations() => new(Options<ConfirmationsDbContext>(ConfirmationsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var covers = Sittings();
        await using var confirmations = Confirmations();
        await orders.Database.MigrateAsync();
        await covers.Database.MigrateAsync();
        await confirmations.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "sittings.\"InboxMessages\", sittings.\"OutboxMessages\", sittings.\"Waitlist\", sittings.\"CoverHolds\", sittings.\"SittingGuests\", sittings.\"Sittings\", " +
            "confirmations.\"InboxMessages\", confirmations.\"OutboxMessages\", confirmations.\"Confirmations\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Sitting> AddSittingAsync(int coversTotal, long priceCents = 5_000, int? maxPerGuest = null)
    {
        await using var db = Sittings();
        var sitting = new Sitting
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Sitting",
            Room = "Terrace",
            Chef = "Chez Marlo",
            ServiceDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            CoversTotal = coversTotal,
            CoversLeft = coversTotal,
            MaxPerGuest = maxPerGuest ?? coversTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sittings.Add(sitting);
        await db.SaveChangesAsync();
        return sitting;
    }

    public static async Task<Order> AddOrderAsync(Sitting sitting, string reference, int covers, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            SittingId = sitting.Id,
            SittingCode = sitting.Code,
            Reference = reference,
            Covers = covers,
            TotalCents = OrderRules.TotalCents(sitting.PriceCents, covers),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Sitting[] sittings) : ISittingCatalog
{
    public Task<SittingInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(sittings.Where(s => s.Code == code).Select(s => new SittingInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
