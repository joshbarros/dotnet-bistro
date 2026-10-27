using Bistro.Shared;
using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bistro.Confirmations;

public static class ConfirmationsModule
{
    public static IServiceCollection AddConfirmationsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Bistro") ?? throw new InvalidOperationException("Connection string 'Bistro' is not configured.");
        services.AddDbContext<ConfirmationsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ConfirmationsDbContext.SchemaName)));
        services.AddScoped<ConfirmationsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ConfirmationsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ConfirmationsDbContext>>("confirmations-db", tags: ["ready"]);

        messaging.AddOutbox<ConfirmationsDbContext>()
            .AddConsumer<ConfirmationsHandler>(Topology.ConfirmationsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
