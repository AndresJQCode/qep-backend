using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Infrastructure.Excel;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Modules.ServiceContracts.Infrastructure.Reminders;

namespace Modules.ServiceContracts.Infrastructure;

public static class ServiceContractsInfrastructureExtensions
{
    public static IServiceCollection AddServiceContractsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<ServiceContractsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "billing")));

        services.AddScoped<IServiceContractRepository, ServiceContractRepository>();
        services.AddScoped<IChargeRepository, ChargeRepository>();
        services.AddScoped<ICustomerBillingProfileRepository, CustomerBillingProfileRepository>();
        services.AddScoped<IServiceContractsSettingsRepository, ServiceContractsSettingsRepository>();
        services.AddScoped<IServiceContractsUnitOfWork, ServiceContractsUnitOfWork>();
        services.AddScoped<IServiceContractsAuditPublisher, ServiceContractsAuditPublisher>();

        // Importación desde Excel (§3.2b), como los de Customers.
        services.AddScoped<IExcelServiceContractsImporter, ClosedXmlServiceContractsImporter>();
        services.AddScoped<IServiceContractsImportTemplateBuilder, ClosedXmlServiceContractsImportTemplateBuilder>();
        services.AddSingleton<IServiceContractsImportFailureLog, ServiceContractsImportFailureLog>();

        // El worker diario (spec §3.3, §6): opciones validadas al arrancar, como QuotationsOptions.
        services.AddOptions<ServiceContractsOptions>().Bind(configuration.GetSection(ServiceContractsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServiceContractsOptions>, ServiceContractsOptionsValidator>();
        services.AddScoped<TenantRunStore>();
        services.AddScoped<CustomerMessageStore>();
        services.AddScoped<DailyDigestStore>();
        services.AddScoped<IServiceContractsReminderProcessor, ServiceContractsReminderProcessor>();
        services.AddHostedService<ServiceContractsReminderWorker>();

        return services;
    }
}
