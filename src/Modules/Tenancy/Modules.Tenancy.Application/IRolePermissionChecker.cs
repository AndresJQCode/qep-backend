namespace Modules.Tenancy.Application;

/// <summary>
/// Resolves whether any of a set of membership role references grants a given
/// permission. Owned by Tenancy so membership invariants (e.g. a tenant must retain
/// a member able to manage memberships) can be enforced without Tenancy depending on
/// Authorization's role catalog internals; implemented by Authorization against its
/// own <c>IRoleCatalog</c>, mirroring the <see cref="IMembershipDirectory"/> contract
/// direction (ADR 0016 module boundaries).
/// </summary>
public interface IRolePermissionChecker
{
    bool AnyGrants(IReadOnlyCollection<string> roles, string permission);
}
