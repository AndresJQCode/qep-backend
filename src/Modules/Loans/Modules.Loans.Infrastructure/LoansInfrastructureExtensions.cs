using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Loans.Application;
using Modules.Loans.Infrastructure.Generation;
using Modules.Loans.Infrastructure.Persistence;

namespace Modules.Loans.Infrastructure;

public static class LoansInfrastructureExtensions
{
    public static IServiceCollection AddLoansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<LoansDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "loans")));

        services.AddScoped<ILoanRepository, LoanRepository>();
        services.AddScoped<ILoanInstallmentRepository, LoanInstallmentRepository>();
        services.AddScoped<ILoanCapitalPaymentRepository, LoanCapitalPaymentRepository>();
        services.AddScoped<ILoansUnitOfWork, LoansUnitOfWork>();
        services.AddScoped<ILoansAuditPublisher, LoansAuditPublisher>();

        // El worker diario de generación de cuotas: opciones validadas al arrancar.
        services.AddOptions<LoansOptions>().Bind(configuration.GetSection(LoansOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LoansOptions>, LoansOptionsValidator>();
        services.AddScoped<TenantRunStore>();
        services.AddScoped<ILoansGenerationProcessor, LoansGenerationProcessor>();
        services.AddHostedService<LoansGenerationWorker>();

        return services;
    }
}
