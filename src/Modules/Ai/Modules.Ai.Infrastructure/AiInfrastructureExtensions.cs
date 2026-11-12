using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.Infrastructure;

public static class AiInfrastructureExtensions
{
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Plan 2026-11-12, C-3: la lista curada por proveedor. Rota, el pod no arranca: un agente no podría publicarse.
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IAiModelCatalog, ConfiguredAiModelCatalog>();
        return services;
    }
}
