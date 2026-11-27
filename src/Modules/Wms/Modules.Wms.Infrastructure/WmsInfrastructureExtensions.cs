using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Excel;
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
        services.AddScoped<IUserReferenceProbe, WmsUserReferenceProbe>();
        services.AddScoped<IWmsProvisioner, WmsProvisioner>();
        services.AddScoped<IWarehouseRepository, WarehouseRepository>();
        services.AddScoped<IOwnerRepository, OwnerRepository>();
        services.AddScoped<IUnitOfMeasureRepository, UnitOfMeasureRepository>();
        services.AddScoped<IVariantProfileRepository, VariantProfileRepository>();
        services.AddScoped<IIdentifierRepository, IdentifierRepository>();
        services.AddScoped<ILotRepository, LotRepository>();
        services.AddScoped<IBalanceChangeQueue, BalanceChangeQueue>();
        services.AddScoped<IBalanceReconciliation, BalanceReconciliation>();
        services.AddScoped<IPartitionMaintenance, PartitionMaintenance>();
        services.AddScoped<IInventoryLedger, InventoryLedger>();
        services.AddScoped<StockReadQueries>();
        services.AddScoped<IStockReadQueries>(provider => provider.GetRequiredService<StockReadQueries>());
        services.AddScoped<IMovementReader>(provider => provider.GetRequiredService<StockReadQueries>());
        services.AddSingleton<WmsMetrics>();
        // Fábrica tipada con el concreto: el inventario multi-réplica (spec 2026-10-29 D-6) así ve el tipo.
        services.AddSingleton<IWmsConfirmMetrics, WmsMetrics>(provider => provider.GetRequiredService<WmsMetrics>());
        services.AddScoped<IAdjustmentRepository, AdjustmentRepository>();
        services.AddScoped<IWarehouseTaskRepository, WarehouseTaskRepository>();
        services.AddScoped<IWmsEventPublisher, WmsEventPublisher>();
        services.AddScoped<AdjustmentDtoComposer>();
        services.AddScoped<WarehouseTaskDtoComposer>();
        services.AddScoped<SupervisorTaskRunner>();
        services.AddScoped<OperatorTaskGuard>();
        services.AddScoped<TaskClosedNotifier>();
        services.AddScoped<OperatorLineRunner>();
        services.AddScoped<IImportPreviewStore, ImportPreviewStore>();
        services.AddSingleton<IInitialBalanceSheetReader, ClosedXmlInitialBalanceSheetReader>();
        services.AddSingleton<IStockExportBuilder, ClosedXmlStockExportBuilder>();

        // Idempotencia (S2): el scope lo fija el filtro del API y lo consume Record en la transacción del handler.
        services.AddScoped<IdempotencyScope>();
        services.AddScoped<IOperationReceipts, OperationReceipts>();

        // Workers de WMS: opciones validadas al arrancar. Las pruebas de integración fijan intervalos enormes para que ninguno corra solo.
        services.AddOptions<WmsWorkerOptions>().Bind(configuration.GetSection(WmsWorkerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<WmsWorkerOptions>, WmsWorkerOptionsValidator>();
        services.AddSingleton<WmsHousekeepingWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<WmsHousekeepingWorker>());
        services.AddSingleton<BalanceChangePublisher>();
        services.AddHostedService(provider => provider.GetRequiredService<BalanceChangePublisher>());
        services.AddSingleton<BalanceReconciliationWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BalanceReconciliationWorker>());
        services.AddSingleton<PartitionMaintenanceWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<PartitionMaintenanceWorker>());
        services.AddSingleton<TaskLeaseSweeper>();
        services.AddHostedService(provider => provider.GetRequiredService<TaskLeaseSweeper>());

        // Las sondas de «con existencias» leen el libro de saldos. Las pruebas las sustituyen con configureServices para ejercer las reglas.
        services.AddScoped<IVariantStockProbe, VariantStockProbe>();
        services.AddScoped<ILocationStockProbe, LocationStockProbe>();
        services.AddScoped<IOwnerStockProbe, OwnerStockProbe>();

        // El gateway hacia qcode-pdf lo registra el composition root: la clave es de Quotations y este módulo no la ve.
        services.AddScoped<IWmsLabelRenderer, LocationLabelRenderer>();

        return services;
    }
}
