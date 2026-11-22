using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Infrastructure.Persistence;

namespace Modules.Payroll.Infrastructure;

public static class PayrollInfrastructureExtensions
{
    public static IServiceCollection AddPayrollInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PayrollDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    PayrollDbContext.Schema)));

        services.AddScoped<IPayrollUnitOfWork, PayrollUnitOfWork>();
        services.AddScoped<IPayrollAuditPublisher, PayrollAuditPublisher>();
        services.AddScoped<ICompanyPayrollSettingsRepository, CompanyPayrollSettingsRepository>();
        services.AddScoped<IPayrollConceptRepository, PayrollConceptRepository>();

        return services;
    }
}
