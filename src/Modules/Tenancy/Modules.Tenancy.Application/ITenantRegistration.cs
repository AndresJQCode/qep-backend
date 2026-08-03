namespace Modules.Tenancy.Application;

public sealed record TenantRegistrationData(
    string DisplayName,
    string Slug,
    string DefaultCulture,
    string TimeZone,
    string DateFormat);

/// <summary>
/// Creates a new tenant and its owner membership in one unit of work (ADR 0017).
/// Only reachable when public tenant signup is enabled; the caller is responsible
/// for provisioning the owner user first and enforcing the feature flag.
/// </summary>
public interface ITenantRegistration
{
    /// <returns>The id of the created tenant.</returns>
    Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId,
        TenantRegistrationData data,
        string correlationId,
        CancellationToken cancellationToken);
}
