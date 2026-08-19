using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Persistence;

namespace Modules.Customers.Infrastructure;

public static class CustomersInfrastructureExtensions
{
    public static IServiceCollection AddCustomersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<CustomersDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "customers")));

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICustomersUnitOfWork, CustomersUnitOfWork>();
        services.AddScoped<ICustomersAuditPublisher, CustomersAuditPublisher>();
        services.AddScoped<ICucGenerator, CucGenerator>();

        return services;
    }
}
