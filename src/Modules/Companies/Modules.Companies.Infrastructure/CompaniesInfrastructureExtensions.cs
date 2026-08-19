using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Application;
using Modules.Companies.Infrastructure.Persistence;

namespace Modules.Companies.Infrastructure;

public static class CompaniesInfrastructureExtensions
{
    public static IServiceCollection AddCompaniesInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<CompaniesDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "companies")));

        services.AddScoped<ICompanyRepository, CompanyRepository>();
        services.AddScoped<ICompaniesUnitOfWork, CompaniesUnitOfWork>();
        services.AddScoped<ICompaniesAuditPublisher, CompaniesAuditPublisher>();

        return services;
    }
}
