using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Authentication;

internal sealed class HttpExecutionContext(IHttpContextAccessor httpContextAccessor)
    : IExecutionContext
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("No active HTTP execution context.");

    // Prefer the internal QEP user id (resolved from the provider subject for external
    // tokens). The dev stub sets the QEP subject directly in "sub".
    public Guid SubjectId => ParseRequiredGuid(
        User.FindFirstValue(QepClaimTypes.QepSubject) is not null
            ? QepClaimTypes.QepSubject
            : QepClaimTypes.SubjectId);

    public TenantId TenantId => new(ParseRequiredGuid(QepClaimTypes.TenantId));

    public bool HasPermission(string permission) =>
        User.Claims.Any(claim =>
            claim.Type == QepClaimTypes.Permission &&
            StringComparer.Ordinal.Equals(claim.Value, permission));

    private Guid ParseRequiredGuid(string claimType)
    {
        var value = User.FindFirstValue(claimType);
        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Authenticated principal is missing a valid '{claimType}' claim.");
    }
}
