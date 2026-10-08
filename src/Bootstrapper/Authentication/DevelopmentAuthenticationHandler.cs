using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Authentication;

// Los handlers de autenticación se resuelven por request, así que admiten dependencias scoped como
// ITenantModules.
internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITenantModules tenantModules,
    ModuleEntitlementMask entitlementMask,
    IOperatorTenant operatorTenant,
    ITenantDirectory tenantDirectory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "Development";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subjectId = Request.Headers["X-Subject-Id"].ToString();
        var tenantId = Request.Headers["X-Tenant-Id"].ToString();
        if (!Guid.TryParse(subjectId, out _) || !Guid.TryParse(tenantId, out var parsedTenantId))
        {
            // Sin tenant no hay consulta de módulos: el request ni siquiera autentica.
            return AuthenticateResult.Fail(
                "Development requests require valid X-Subject-Id and X-Tenant-Id headers.");
        }

        List<Claim> claims = [new(QepClaimTypes.SubjectId, subjectId)];

        // Spec 2026-10-08 §4: con fila y no Active, ni claim de tenant ni permisos — el mismo efecto que
        // el camino real. Sin fila (tenant simulado), sin cambio.
        var status = await tenantDirectory.GetStatusAsync(new TenantId(parsedTenantId), Context.RequestAborted);
        if (status is null or TenantStatus.Active)
        {
            claims.Add(new Claim(QepClaimTypes.TenantId, tenantId));

            // Spec 2026-10-07, «Cuándo el stub enmascara»: el criterio es si el tenant tiene fila en
            // tenancy.tenants. Sin fila (tenant simulado por casi todas las suites de Catalog, Customers
            // y Companies) los permisos quedan como vienen; con fila, se enmascaran igual que por cookie.
            var requested = ResolvePermissions();
            var modules = await tenantModules.FindAsync(parsedTenantId, Context.RequestAborted);
            var masked = modules is null ? requested : entitlementMask.Apply(requested, modules);
            // Spec 2026-10-08 §2: siempre, exista o no la fila del tenant. El stub no puede autodeclararse
            // operador con X-Permissions.
            foreach (var permission in OperatorPermissionFilter.Apply(masked, operatorTenant.IsOperator(parsedTenantId)))
            {
                claims.Add(new Claim(QepClaimTypes.Permission, permission));
            }
        }

        // Claims de identidad opcionales que simulan un token de proveedor OIDC para el
        // flujo de login de /auth/session. Ausentes en los requests normales con tenant.
        var email = Request.Headers["X-Email"].ToString();
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("email", email));
            var emailVerified = Request.Headers["X-Email-Verified"].ToString();
            claims.Add(new Claim(
                "email_verified",
                string.IsNullOrWhiteSpace(emailVerified) ? "true" : emailVerified));
        }

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, AuthenticationSchemeName));
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, AuthenticationSchemeName));
    }

    // Los permisos vienen del header opcional X-Permissions (separados por coma) para
    // poder simular un sujeto de sólo lectura en desarrollo y en pruebas. Cuando el
    // header no está se conceden los permisos de tenancy por defecto, preservando la
    // experiencia por defecto del developer. Esto es un stub — ver
    // docs/decisions/0001-development-auth-stub.md.
    private IEnumerable<string> ResolvePermissions()
    {
        var header = Request.Headers["X-Permissions"].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return
            [
                TenancyPermissions.SettingsRead,
                TenancyPermissions.SettingsUpdate,
                TenancyPermissions.AdvisorshipInvite,
                TenancyPermissions.AdvisorshipRead,
                TenancyPermissions.AdvisorshipManage
            ];
        }

        return header
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);
    }
}
