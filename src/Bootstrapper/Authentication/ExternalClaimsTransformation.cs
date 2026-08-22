using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Modules.Authorization.Application;
using Modules.Identity.Application;

namespace Bootstrapper.Authentication;

/// <summary>
/// Enriquece un principal autenticado externamente (un token de proveedor ya validado) con
/// los claims internos de identidad y acceso de QEP que la aplicación espera:
/// <list type="bullet">
/// <item>resuelve el <c>sub</c> del proveedor al id interno de usuario (<c>qep_sub</c>);</item>
/// <item>valida el tenant activo que viene en el header <c>X-Tenant-Id</c> contra una
/// membresía viva y agrega los claims <c>tenant_id</c> y de permisos que resuelve la
/// capacidad Authorization.</item>
/// </list>
/// El principal del stub de desarrollo queda intacto.
/// </summary>
internal sealed class ExternalClaimsTransformation(
    IHttpContextAccessor httpContextAccessor,
    IProviderIdentityResolver identityResolver,
    IAuthorizationService authorizationService)
    : IClaimsTransformation
{
    // Primer (y único) proveedor externo, según el ADR 0014.
    private const string Provider = "google";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity ||
            !identity.IsAuthenticated ||
            identity.AuthenticationType == DevelopmentAuthenticationHandler.AuthenticationSchemeName)
        {
            return principal;
        }

        var cancellationToken =
            httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        // Dos pasos independientes, cada uno con su propia verificación de claim — no un
        // único early-return combinado. Esto tiene que funcionar para dos llamadores distintos:
        //  - el principal de bearer de Google (que sólo usa POST /auth/session) todavía no
        //    tiene qep_sub y necesita el paso 1 para resolverlo;
        //  - el principal de cookie de sesión (que usa todo el resto de los endpoints) ya tiene
        //    qep_sub, puesto directo por SessionCookieAuthenticationHandler, y aun así tiene
        //    que correr el paso 2 para obtener los claims de tenant y permisos. Un único
        //    early-return sobre "ya tiene qep_sub" —como era este método antes— saltearía el
        //    paso 2 para el camino de cookie por completo y rompería en silencio la
        //    autorización de todos los endpoints. Las verificaciones por claim además lo
        //    mantienen idempotente si AuthenticateAsync corre dos veces para el mismo request.
        if (!identity.HasClaim(claim => claim.Type == QepClaimTypes.QepSubject))
        {
            await ResolveSubjectAsync(identity, principal, cancellationToken);
        }

        if (identity.HasClaim(claim => claim.Type == QepClaimTypes.QepSubject) &&
            !identity.HasClaim(claim => claim.Type == QepClaimTypes.TenantId))
        {
            await ResolveTenantAndPermissionsAsync(identity, cancellationToken);
        }

        return principal;
    }

    private async Task ResolveSubjectAsync(
        ClaimsIdentity identity,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var userId = await identityResolver.ResolveUserIdAsync(
            Provider,
            subject,
            cancellationToken);
        if (userId is null)
        {
            // Autenticado con el proveedor pero todavía sin vincular a ningún usuario QEP
            // (por ejemplo antes de /auth/session). Se deja sin enriquecer; los endpoints
            // autorizados van a rechazar.
            return;
        }

        identity.AddClaim(new Claim(QepClaimTypes.QepSubject, userId.Value.ToString()));
    }

    private async Task ResolveTenantAndPermissionsAsync(
        ClaimsIdentity identity,
        CancellationToken cancellationToken)
    {
        var userId = identity.FindFirst(QepClaimTypes.QepSubject)?.Value;
        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            return;
        }

        var tenantHeader = httpContextAccessor.HttpContext?.Request.Headers["X-Tenant-Id"]
            .ToString();
        if (!Guid.TryParse(tenantHeader, out var tenantId))
        {
            return;
        }

        // Los permisos vienen de la capacidad Authorization (deny por defecto,
        // sacados de los roles de la membresía activa). Un resultado nulo significa que no
        // hay membresía activa en ese tenant.
        var permissions = await authorizationService.ResolvePermissionsAsync(
            parsedUserId,
            tenantId,
            cancellationToken);
        if (permissions is null)
        {
            return;
        }

        identity.AddClaim(new Claim(QepClaimTypes.TenantId, tenantId.ToString()));
        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(QepClaimTypes.Permission, permission));
        }
    }
}
