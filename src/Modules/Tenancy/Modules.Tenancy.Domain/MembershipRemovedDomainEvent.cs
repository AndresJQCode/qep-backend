using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed record MembershipRemovedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId) : IDomainEvent;
