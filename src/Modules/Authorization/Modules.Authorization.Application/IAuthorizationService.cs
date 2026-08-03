namespace Modules.Authorization.Application;

public sealed record AuthorizationDecision(bool Allowed, string ReasonCode)
{
    public static AuthorizationDecision Allow() => new(true, "allowed");

    public static AuthorizationDecision Deny(string reasonCode) => new(false, reasonCode);
}

/// <summary>
/// The Authorization capability's public contract (capability-contracts.md). Access
/// decisions are deny-by-default and always tenant-scoped: a subject's permissions
/// come from the roles of its active membership in the tenant. The frontend is never
/// an enforcement point.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>Decides whether the subject may perform <paramref name="permission"/>
    /// in the tenant. Deny-by-default; denies when there is no active membership.</summary>
    Task<AuthorizationDecision> AuthorizeAsync(
        Guid subjectId,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken);

    /// <summary>Resolves the subject's effective permissions in the tenant, or
    /// <c>null</c> when the subject has no active membership there.</summary>
    Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(
        Guid subjectId,
        Guid tenantId,
        CancellationToken cancellationToken);
}
