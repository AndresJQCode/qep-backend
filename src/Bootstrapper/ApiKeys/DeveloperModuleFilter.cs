using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// La capacidad <c>developer</c> apagada cierra toda la API externa con 403 <c>tenancy.module_not_enabled</c> (spec 2026-10-30 §4.3).
/// Falla cerrado: un tenant sin fila de módulos tampoco pasa, a diferencia de <c>TenantModuleGuard.EnsureEnabledAsync</c>, que deja
/// pasar al tenant simulado del stub de desarrollo.
/// </summary>
public sealed class DeveloperModuleFilter(ITenantModules modules, IApiCaller caller) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await TenantModuleGuard.EnsureEnabledFailClosedAsync(
            modules, caller.TenantId, TenantModuleKeys.Developer, context.HttpContext.RequestAborted);
        return await next(context);
    }
}
