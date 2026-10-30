using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Billing.Application;
using Modules.Billing.Infrastructure.Excel;
using Modules.Billing.Infrastructure.Persistence;
using Modules.Billing.Infrastructure.Reminders;

namespace Modules.Billing.Infrastructure;

public static class BillingInfrastructureExtensions
{
    public static IServiceCollection AddBillingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<BillingDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "billing")));

        services.AddScoped<IBillingServiceRepository, BillingServiceRepository>();
        services.AddScoped<IChargeRepository, ChargeRepository>();
        services.AddScoped<ICustomerBillingProfileRepository, CustomerBillingProfileRepository>();
        services.AddScoped<IBillingSettingsRepository, BillingSettingsRepository>();
        services.AddScoped<IBillingUnitOfWork, BillingUnitOfWork>();
        services.AddScoped<IBillingAuditPublisher, BillingAuditPublisher>();

        // Importación desde Excel (§3.2b), como los de Customers.
        services.AddScoped<IExcelBillingImporter, ClosedXmlBillingImporter>();
        services.AddScoped<IBillingImportTemplateBuilder, ClosedXmlBillingImportTemplateBuilder>();
        services.AddSingleton<IBillingImportFailureLog, BillingImportFailureLog>();

        // El worker diario (spec §3.3, §6): opciones validadas al arrancar, como QuotationsOptions.
        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BillingOptions>, BillingOptionsValidator>();
        services.AddScoped<TenantRunStore>();
        services.AddScoped<CustomerMessageStore>();
        services.AddScoped<DailyDigestStore>();
        services.AddScoped<IBillingReminderProcessor, BillingReminderProcessor>();
        services.AddHostedService<BillingReminderWorker>();

        return services;
    }
}
