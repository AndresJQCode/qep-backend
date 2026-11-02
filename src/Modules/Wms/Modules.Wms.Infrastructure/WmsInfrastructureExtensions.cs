using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Pdf;
using Modules.Wms.Infrastructure.Persistence;
using Modules.Wms.Infrastructure.Workers;

namespace Modules.Wms.Infrastructure;

public static class WmsInfrastructureExtensions
{
    public static IServiceCollection AddWmsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<WmsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WmsDbContext.Schema)));

        services.AddScoped<WmsAuthorization>();
        services.AddScoped<WmsCaller>();
        services.AddScoped<IWmsUnitOfWork, WmsUnitOfWork>();
        services.AddScoped<IWmsAuditRecorder, WmsAuditRecorder>();
        services.AddScoped<IWmsProvisioner, WmsProvisioner>();
        services.AddScoped<IWarehouseRepository, WarehouseRepository>();
        services.AddScoped<IOwnerRepository, OwnerRepository>();
        services.AddScoped<IUnitOfMeasureRepository, UnitOfMeasureRepository>();
        services.AddScoped<IVariantProfileRepository, VariantProfileRepository>();
        services.AddScoped<IIdentifierRepository, IdentifierRepository>();
        services.AddScoped<ILotRepository, LotRepository>();
        services.AddScoped<IBalanceChangeQueue, BalanceChangeQueue>();
        services.AddScoped<IInventoryLedger, InventoryLedger>();
        services.AddScoped<IStockReadQueries, StockReadQueries>();
        services.AddSingleton<WmsMetrics>();
        services.AddSingleton<IWmsConfirmMetrics>(provider => provider.GetRequiredService<WmsMetrics>());
        services.AddScoped<IAdjustmentRepository, AdjustmentRepository>();
        services.AddScoped<IWmsEventPublisher, WmsEventPublisher>();
        services.AddScoped<AdjustmentDtoComposer>();

        // Idempotencia (S2): el scope lo fija el filtro del API y lo consume Record en la transacción del handler.
        services.AddScoped<IdempotencyScope>();
        services.AddScoped<IOperationReceipts, OperationReceipts>();

        // Workers de WMS: opciones validadas al arrancar. Las pruebas de integración fijan intervalos enormes para que ninguno corra solo.
        services.AddOptions<WmsWorkerOptions>().Bind(configuration.GetSection(WmsWorkerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<WmsWorkerOptions>, WmsWorkerOptionsValidator>();
        services.AddSingleton<WmsHousekeepingWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<WmsHousekeepingWorker>());

        // Sin libro de inventario no hay existencias: WMS-D reemplaza estas tres por consultas al libro.
        services.AddScoped<IVariantStockProbe, NoVariantStockProbe>();
        services.AddScoped<ILocationStockProbe, NoLocationStockProbe>();
        services.AddScoped<IOwnerStockProbe, NoOwnerStockProbe>();

        // El gateway hacia qcode-pdf lo registra el composition root: la clave es de Quotations y este módulo no la ve.
        services.AddScoped<IWmsLabelRenderer, LocationLabelRenderer>();

        return services;
    }
}
