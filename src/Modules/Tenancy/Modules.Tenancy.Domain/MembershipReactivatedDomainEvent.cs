using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// Un administrador devolvió a activa una membresía suspendida.
///
/// Es su propio evento, no una reutilización del de invitación: lo que pasó acá es que
/// alguien deshizo una suspensión, y un rastro de auditoría que no puede distinguir eso de
/// una invitación nueva pierde el único hecho que valía la pena guardar.
/// </summary>
public sealed record MembershipReactivatedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId) : IDomainEvent;
