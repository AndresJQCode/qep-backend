using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.Infrastructure;

public static class GeographyInfrastructureExtensions
{
    public static IServiceCollection AddGeographyInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<GeographyDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "geography")));

        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<ICityRepository, CityRepository>();
        services.AddScoped<ICitySynonymRepository, CitySynonymRepository>();
        services.AddScoped<IUnresolvedCityRepository, UnresolvedCityRepository>();
        // Un puerto y no un caso de uso del dispatcher: lo llama Bootstrapper (SC-06, SC-13), sin sesión ni tenant.
        services.AddScoped<IUnresolvedCityReporter, UnresolvedCityReporter>();
        services.AddScoped<IGeographyUnitOfWork, GeographyUnitOfWork>();
        services.AddScoped<IGeographyAuditRecorder, GeographyAuditRecorder>();
        // Spec 2026-10-26 D-C6: los eventos de sinónimos, en el outbox de plataforma dentro de la transacción de Geography.
        services.AddScoped<IGeographyEventPublisher, GeographyEventPublisher>();
        services.AddScoped<GeographySeeder>();

        return services;
    }
}
