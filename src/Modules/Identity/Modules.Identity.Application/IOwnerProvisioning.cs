namespace Modules.Identity.Application;

/// <summary>
/// Provisions the owner user of a self-registered tenant (ADR 0017). Unlike
/// <see cref="IProviderLinking"/> this is NOT invitation-gated: bootstrapping a new
/// tenant necessarily creates its first user without a prior invitation. It is the
/// single documented exception to invitation-only provisioning and is only reachable
/// when public tenant signup is enabled. A verified email is still required.
/// </summary>
public interface IOwnerProvisioning
{
    Task<Guid> ProvisionOwnerAsync(
        string provider,
        string subject,
        string email,
        CancellationToken cancellationToken);
}
