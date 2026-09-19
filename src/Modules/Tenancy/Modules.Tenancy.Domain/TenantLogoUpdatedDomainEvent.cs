using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// `LogoFileId = null` significa "se quitó el logo": no hay un evento aparte para no duplicar el
/// mapeo de <c>OutboxWriter</c> (decisión 11 del spec 2026-09-19). No reusa
/// <see cref="TenantSettingsUpdatedDomainEvent"/> porque <c>TenantSettingsChangeLogProjection</c>
/// lo proyectaría como un cambio de configuración, y el nombre del evento mentiría.
/// </summary>
public sealed record TenantLogoUpdatedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    TenantId TenantId,
    long Version,
    Guid? LogoFileId) : IDomainEvent;
