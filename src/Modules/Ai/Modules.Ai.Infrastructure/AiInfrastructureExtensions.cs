using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Options;
using Modules.Ai.Infrastructure.Persistence;

namespace Modules.Ai.Infrastructure;

public static class AiInfrastructureExtensions
{
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Plan 2026-11-12, C-3: la lista curada por proveedor. Rota, el pod no arranca: un agente no podría publicarse.
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IAiModelCatalog, ConfiguredAiModelCatalog>();

        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        services.AddDbContext<AiDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema)));
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IAiUnitOfWork, AiUnitOfWork>();
        services.AddScoped<IAiAuditRecorder, AiAuditRecorder>();
        // La membresía de quien llama, una vez por request: autor del borrador y de la publicación.
        services.AddScoped<AiCaller>();
        services.AddScoped<AgentEditorBuilder>();
        return services;
    }
}
