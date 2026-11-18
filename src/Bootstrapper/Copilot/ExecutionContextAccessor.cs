using Bootstrapper.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;

namespace Bootstrapper.Copilot;

/// <summary>
/// Plan fase 2, C2.4: <see cref="IExecutionContext"/> deja de ser directamente <see cref="HttpExecutionContext"/>. Por scope: el contexto
/// delegado si el scope lo trae (el del ejecutor del copiloto) y, si no, el de los claims del request, como siempre.
/// </summary>
public static class ExecutionContextAccessor
{
    public static IServiceCollection AddQepExecutionContext(this IServiceCollection services)
    {
        services.AddScoped<HttpExecutionContext>();
        services.AddScoped<DelegatedExecutionScope>();
        services.AddScoped<IExecutionContext>(Resolve);
        return services;
    }

    private static IExecutionContext Resolve(IServiceProvider provider) =>
        provider.GetRequiredService<DelegatedExecutionScope>().Context ?? (IExecutionContext)provider.GetRequiredService<HttpExecutionContext>();
}
