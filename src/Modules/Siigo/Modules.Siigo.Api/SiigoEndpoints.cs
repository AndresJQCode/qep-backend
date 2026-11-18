using Microsoft.AspNetCore.Routing;

namespace Modules.Siigo.Api;

/// <summary>
/// Las rutas de Siigo dentro de su tenant (spec 2026-11-18 §5). Tenant en la ruta: la política frena a quien no tiene el permiso (y,
/// con el módulo apagado, <c>ModuleEntitlementMask</c> ya se lo quitó) y el handler (<c>SiigoAuthorization</c>) a quien lo tiene en
/// otro tenant. Todavía sin rutas: llegan con la configuración y la facturación.
/// </summary>
public static class SiigoEndpoints
{
    public static IEndpointRouteBuilder MapSiigoEndpoints(this IEndpointRouteBuilder endpoints) => endpoints;
}
