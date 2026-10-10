using System.Security.Claims;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

public static class TenantModulesEndpoints
{
    public static IEndpointRouteBuilder MapTenantModulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sólo autenticación, sin permiso, igual que /authorization/me y por la misma razón: cualquier
        // miembro activo lo necesita para dibujar su pantalla (spec 2026-10-07, «Endpoint de
        // capacidades»).
        endpoints
            .MapGet("/api/v1/tenants/{tenantId:guid}/modules", GetModulesAsync)
            .WithTags("Tenancy")
            .RequireAuthorization()
            .Produces<TenantModulesResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetModulesAsync(
        Guid tenantId,
        HttpContext httpContext,
        ITenantModules tenantModules,
        CancellationToken cancellationToken)
    {
        // El claim directo y no IExecutionContext.TenantId, que tira 500 sin claim
        // (HttpExecutionContext.cs:21). Que no esté merece un 403.
        var user = httpContext.User;
        var claimedTenant = user.FindFirstValue(QepClaimTypes.TenantId);
        if (!Guid.TryParse(claimedTenant, out var authenticatedTenant) || authenticatedTenant != tenantId)
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the modules of this tenant.");
        }

        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        return Results.Ok(TenantModulesResponse.From(
            tenantId, modules, QepAuthenticationMode.IsDevelopmentStub(user)));
    }
}

/// <summary>
/// Regla BFF (spec 2026-10-07): **siempre todas, en el orden de <c>TenantModuleKeys.All</c>,
/// aunque estén apagadas** — si faltara una, la SPA tendría que conocer la lista del backend para
/// dibujar la que no vino.
/// </summary>
public sealed record TenantModulesResponse(Guid TenantId, IReadOnlyList<TenantModuleResponse> Modules)
{
    /// <summary>
    /// Con <paramref name="modules"/> en <c>null</c> (el tenant no está en <c>tenancy.tenants</c>), la
    /// respuesta depende del esquema: bajo el stub de desarrollo, todo prendido y contratado, que es lo
    /// que el stub hace con los permisos; bajo cualquier otro, todo apagado y sin contratar, el mismo
    /// fail closed de <c>AuthorizationService</c>: la pantalla y los permisos dicen lo mismo.
    /// </summary>
    public static TenantModulesResponse From(Guid tenantId, TenantModuleSet? modules, bool isDevelopmentStub)
    {
        var set = modules ?? (isDevelopmentStub
            ? TenantModuleSet.FromStored(TenantModuleKeys.All)
            : TenantModuleSet.Empty);

        return new TenantModulesResponse(
            tenantId,
            TenantModuleKeys.All
                .Select(key => new TenantModuleResponse(
                    key.Value,
                    set.IsEnabled(key),
                    set.IsContracted(key),
                    set.MissingDependencies(key).Select(dependency => dependency.Value).ToArray()))
                .ToArray());
    }
}

/// <param name="Enabled">El efectivo: contratado y con sus dependencias prendidas.</param>
/// <param name="Contracted">Que la fila existe. Sin los dos, la pantalla no puede distinguir «no está
/// en tu plan» de «está, pero le falta otro».</param>
/// <param name="MissingDependencies">Las causas raíz —dependencias transitivas sin contratar—, sólo
/// con <c>Contracted</c> y sin <c>Enabled</c>. Así la pantalla nombra lo que hay que contratar y no
/// un intermediario que ya está contratado. Viaja por clave: las etiquetas las tiene la SPA.</param>
public sealed record TenantModuleResponse(
    string Key,
    bool Enabled,
    bool Contracted,
    IReadOnlyList<string> MissingDependencies);
