namespace Modules.Tenancy.Application;

public sealed record TenantRegistrationData(
    string DisplayName,
    string Slug,
    string DefaultCulture,
    string TimeZone,
    string DateFormat);

/// <summary>
/// Crea un tenant nuevo y su membresía de owner en una sola unidad de trabajo (ADR 0017).
/// Sólo es alcanzable cuando el signup público de tenants está habilitado; el llamador es
/// responsable de aprovisionar primero el usuario owner y de hacer cumplir el feature flag.
/// </summary>
public interface ITenantRegistration
{
    /// <returns>El id del tenant creado.</returns>
    Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId,
        TenantRegistrationData data,
        string correlationId,
        CancellationToken cancellationToken);
}
