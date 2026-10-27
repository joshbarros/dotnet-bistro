using Bistro.Shared;
using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bistro.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Bistro") ?? throw new InvalidOperationException("Connection string 'Bistro' is not configured.");
        services.AddDbContext<OrdersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.SchemaName)));
        services.Configure<OrderOptions>(config.GetSection("Orders"));
        services.AddScoped<OrderService>();
        services.AddScoped<OrdersHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<OrdersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<OrdersDbContext>>("orders-db", tags: ["ready"]);

        messaging.AddOutbox<OrdersDbContext>()
            .AddConsumer<OrdersHandler>(Topology.OrdersQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.WaitlistedKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
