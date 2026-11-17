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
        return services;
    }
}
