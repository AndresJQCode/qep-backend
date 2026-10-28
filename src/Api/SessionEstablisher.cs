using Bootstrapper.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Authorization.Application;
using Modules.Catalog.Application;
using Modules.Identity.Application;
using Modules.Identity.Infrastructure;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

/// <summary>
/// Accepts the invited memberships, issues the session, writes the cookie and builds the
/// <see cref="SessionResponse"/> body. Shared by every endpoint that establishes a session, so all of
/// them return the identical body.
/// </summary>
internal sealed class SessionEstablisher(
    IMembershipActivation membershipActivation,
    IActiveTenantsQuery activeTenantsQuery,
    ITenantRoleCatalog roleCatalog,
    ITenantDirectory tenantDirectory,
    ICurrenciesInUse currenciesInUse,
    ISessionService sessionService,
    IOptions<QepSessionOptions> sessionOptions,
    IHostEnvironment environment)
{
    public async Task<IResult> EstablishAsync(
        HttpContext httpContext,
        Guid userId,
        string? email,
        CancellationToken cancellationToken)
    {
        // El valor de retorno se descarta a propósito: activeTenantsQuery.ListActiveTenantsAsync
        // de abajo es la única consulta detrás de los dos campos de SessionResponse, para que
        // ids y nombres no puedan discrepar si una membresía cambia entre dos consultas
        // separadas.
        await membershipActivation.AcceptInvitedMembershipsAsync(
            userId,
            httpContext.TraceIdentifier,
            cancellationToken);
        // Antes de emitir la sesión: si leer el catálogo falla, que no quede una cookie viva
        // detrás de una respuesta de error.
        var body = await DescribeAsync(userId, email, cancellationToken);

        var issued = await sessionService.IssueAsync(
            userId,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        SessionCookieWriter.Append(httpContext, sessionOptions.Value, environment, issued);

        return Results.Ok(body);
    }

    public async Task<SessionResponse> DescribeAsync(
        Guid userId,
        string? email,
        CancellationToken cancellationToken)
    {
        var activeTenants = await activeTenantsQuery.ListActiveTenantsAsync(userId, cancellationToken);
        var activeTenantIds = activeTenants.Select(tenant => tenant.TenantId).ToArray();
        var tenants = await ToActiveTenantResponsesAsync(activeTenants, cancellationToken);
        return new SessionResponse(userId, email, activeTenantIds, tenants, Currencies.All);
    }

    /// <summary>
    /// Le pone a cada rol de cada tenant activo el nombre que ve la persona, y a cada tenant su
    /// formato regional.
    /// </summary>
    /// <remarks>
    /// Se resuelve acá y no en Tenancy porque el catálogo de roles es de Authorization, que ya
    /// referencia a Tenancy: consultarlo desde Tenancy cerraría un ciclo. El catálogo es por
    /// tenant —los de sistema más los custom de ese tenant (TenantRoleCatalog.cs:62-110)—, así
    /// que se pide uno por tenant.
    ///
    /// Una clave que el catálogo no conoce sale con la clave como nombre y no se descarta:
    /// puede ser un rol retirado que la membresía todavía nombra, el mismo caso que
    /// TenantRoleCatalog.cs:32-34 tolera sin fallar. Descartarla escondería un rol que la
    /// membresía sí tiene. El orden es el de la membresía, no el del catálogo.
    /// </remarks>
    private async Task<IReadOnlyCollection<ActiveTenantResponse>> ToActiveTenantResponsesAsync(
        IReadOnlyCollection<ActiveTenantSummary> activeTenants,
        CancellationToken cancellationToken)
    {
        var responses = new List<ActiveTenantResponse>(activeTenants.Count);
        foreach (var tenant in activeTenants)
        {
            var catalog = await roleCatalog.ListRolesAsync(tenant.TenantId, cancellationToken);
            var displayNames = catalog.ToDictionary(
                definition => definition.Role,
                definition => definition.DisplayName,
                StringComparer.Ordinal);
            var regional = await tenantDirectory.GetRegionalSettingsAsync(
                new TenantId(tenant.TenantId),
                cancellationToken);
            // Through the port and not through a catalog permission: like the regional format, it is how
            // this tenant's money is shown, and every member needs it before the first paint.
            var inUse = await currenciesInUse.GetAsync(tenant.TenantId, cancellationToken);

            responses.Add(new ActiveTenantResponse(
                tenant.TenantId,
                tenant.DisplayName,
                tenant.Roles
                    .Select(role => new SessionRoleResponse(
                        role,
                        displayNames.GetValueOrDefault(role, role)))
                    .ToArray(),
                regional?.TimeZone,
                regional?.DateFormat,
                regional?.DefaultCurrency,
                regional?.NumberFormat,
                inUse,
                regional?.Country,
                regional?.EffectivePersonTypeRequirement.ToString()));
        }

        return responses;
    }
}
