using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Messaging;
using Modules.Purchases.Infrastructure.Pdf;
using Modules.Purchases.Infrastructure.Persistence;

namespace Modules.Purchases.Infrastructure;

public static class PurchasesInfrastructureExtensions
{
    public static IServiceCollection AddPurchasesInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PurchasesDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    PurchasesDbContext.Schema)));

        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IPurchaseOrderNumberGenerator, PurchaseOrderNumberGenerator>();
        services.AddScoped<IPurchasesUnitOfWork, PurchasesUnitOfWork>();
        services.AddScoped<IPurchasesAuditPublisher, PurchasesAuditPublisher>();
        services.AddScoped<IPurchaseOrderEventPublisher, PurchaseOrderEventPublisher>();

        // Mayorista fase 3: el consumidor de los eventos del pedido mayorista (aprobado, anulado), con reclamo e inbox propio.
        // El aplicador y los adaptadores entre módulos los registra Bootstrapper.
        services.AddSingleton<WholesaleOrderEventsProcessor>();
        services.AddHostedService<WholesaleOrderEventsWorker>();

        // El PDF de la orden, con sección y clave propias de qcode-pdf. Sin la clave en Production el pod no arranca
        // (ValidateOnStart). Singleton por fábrica con un HttpClient propio y su timeout, como SupportPdfRenderer: sin estado.
        services.AddOptions<PurchasesPdfOptions>().Bind(configuration.GetSection(PurchasesPdfOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PurchasesPdfOptions>, PurchasesPdfOptionsValidator>();
        services.AddSingleton<IPurchaseOrderPdfRenderer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PurchasesPdfOptions>>();
            return new PurchaseOrderPdfRenderer(
                new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds) },
                options,
                provider.GetRequiredService<ILogger<PurchaseOrderPdfRenderer>>());
        });

        return services;
    }
}
