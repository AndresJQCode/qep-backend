using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;

namespace Bootstrapper.Authentication;

internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subjectId = Request.Headers["X-Subject-Id"].ToString();
        var tenantId = Request.Headers["X-Tenant-Id"].ToString();
        if (!Guid.TryParse(subjectId, out _) || !Guid.TryParse(tenantId, out _))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Development requests require valid X-Subject-Id and X-Tenant-Id headers."));
        }

        List<Claim> claims =
        [
            new(QepClaimTypes.SubjectId, subjectId),
            new(QepClaimTypes.TenantId, tenantId)
        ];
        foreach (var permission in ResolvePermissions())
        {
            claims.Add(new Claim(QepClaimTypes.Permission, permission));
        }

        // Optional identity claims that simulate an OIDC provider token for the
        // /auth/session login flow. Absent for ordinary tenant-aware requests.
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
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, AuthenticationSchemeName)));
    }

    // Permissions come from the optional X-Permissions header (comma-separated) so
    // a read-only subject can be simulated in development and tests. When the
    // header is absent the default tenancy permissions are granted, preserving the
    // default developer experience. This is a stub — see
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
                TenancyPermissions.MembershipInvite,
                TenancyPermissions.MembershipRead,
                TenancyPermissions.MembershipManage
            ];
        }

        return header
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);
    }
}
