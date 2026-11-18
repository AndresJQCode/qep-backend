using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Suppliers.Infrastructure.Excel;
using Modules.Suppliers.Infrastructure.Persistence;
using Modules.Suppliers.Infrastructure.Phones;

namespace Modules.Suppliers.Infrastructure;

public static class SuppliersInfrastructureExtensions
{
    public static IServiceCollection AddSuppliersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<SuppliersDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "suppliers")));

        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<ISuppliersUnitOfWork, SuppliersUnitOfWork>();
        services.AddScoped<ISuppliersAuditPublisher, SuppliersAuditPublisher>();
        services.AddSingleton<ISupplierPhoneNormalizer, LibPhoneNumberSupplierNormalizer>();
        services.AddSingleton<ISupplierExportBuilder, ClosedXmlSupplierExportBuilder>();
        services.AddScoped<ISupplierExportEventPublisher, SupplierExportEventPublisher>();
        // Spec 2026-11-17 (mayorista): el proveedor vinculado que deja la aceptación de una invitación. Lo consume Wholesale por
        // un adaptador en Bootstrapper.
        services.AddScoped<ISupplierLinking, SupplierLinking>();

        return services;
    }
}
