using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Infrastructure.Persistence;

namespace Modules.Automations.Infrastructure;

public static class AutomationsInfrastructureExtensions
{
    public static IServiceCollection AddAutomationsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddDbContext<AutomationsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AutomationsDbContext.Schema)));

        // La membresía de quien llama, una vez por request: autor del borrador, de la publicación y de la auditoría.
        services.AddScoped<AutomationsCaller>();
        return services;
    }
}
