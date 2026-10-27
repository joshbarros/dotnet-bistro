using Bistro.Shared;
using Bistro.Shared.Contracts;
using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bistro.Sittings;

public static class SittingsModule
{
    public static IServiceCollection AddSittingsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Bistro") ?? throw new InvalidOperationException("Connection string 'Bistro' is not configured.");
        services.AddDbContext<SittingsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", SittingsDbContext.SchemaName)));
        services.AddScoped<SittingsHandler>();
        services.AddScoped<ISittingCatalog, SittingCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<SittingsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<SittingsDbContext>>("covers-db", tags: ["ready"]);

        messaging.AddOutbox<SittingsDbContext>()
            .AddConsumer<SittingsHandler>(Topology.SittingsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
