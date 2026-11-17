using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;

namespace Bootstrapper.Copilot;

/// <summary>Spec 2026-11-17 (copiloto) §6: el ejecutor y la lista cerrada de herramientas. Una herramienta nueva es un adaptador más
/// acá; nada se descubre por reflexión.</summary>
public static class CopilotRegistration
{
    public static IServiceCollection AddCopilotTools(this IServiceCollection services)
    {
        services.AddScoped<ICopilotToolExecutor, CopilotToolExecutor>();
        // Plan fase 2, C2.5: los reportes (CP-3). Sin estado: un singleton por herramienta.
        // C3.1 y C3.2: pedidos y cotizaciones; clientes, catálogo y Mensajería (CP-4).
        foreach (var tool in CopilotReportTools.All().Concat(CopilotDocumentTools.All()).Concat(CopilotDirectoryTools.All()))
        {
            services.AddSingleton(tool);
        }

        return services;
    }
}
