using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Modules.Authorization.Application;
using Modules.Identity.Application;

namespace Bootstrapper.Authentication;

/// <summary>
/// Enriches an externally-authenticated principal (a validated provider token) with
/// the internal QEP identity and access claims the application expects:
/// <list type="bullet">
/// <item>resolves the provider <c>sub</c> to the internal user id (<c>qep_sub</c>);</item>
/// <item>validates the active tenant from the <c>X-Tenant-Id</c> header against a live
/// membership and adds the <c>tenant_id</c> and permission claims resolved by the
/// Authorization capability.</item>
/// </list>
/// The development stub principal is left untouched.
/// </summary>
internal sealed class ExternalClaimsTransformation(
    IHttpContextAccessor httpContextAccessor,
    IProviderIdentityResolver identityResolver,
    IAuthorizationService authorizationService)
    : IClaimsTransformation
{
    // First (and only) external provider, per ADR 0014.
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

        // Two independent steps, each guarded by its own claim check — not a single
        // combined early-return. This has to work for two different callers:
        //  - the Google-bearer principal (used only by POST /auth/session) has no
        //    qep_sub yet and needs step 1 to resolve it;
        //  - the session-cookie principal (used by every other endpoint) already has
        //    qep_sub set directly by SessionCookieAuthenticationHandler and must still
        //    run step 2 to get tenant/permission claims. A single early-return on
        //    "already has qep_sub" — as this method used to be — would skip step 2 for
        //    the cookie path entirely and silently break authorization on every
        //    endpoint. The per-claim guards also keep this idempotent if
        //    AuthenticateAsync happens to run twice for the same request.
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
            // Authenticated with the provider but not linked to any QEP user yet
            // (e.g. before /auth/session). Leave unenriched; authorized endpoints
            // will refuse.
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

        // Permissions come from the Authorization capability (deny-by-default,
        // sourced from the active membership's roles). A null result means no
        // active membership in that tenant.
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
