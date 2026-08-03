using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed record MembershipRolesChangedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId,
    IReadOnlyCollection<string> PreviousRoles,
    IReadOnlyCollection<string> NewRoles) : IDomainEvent;
