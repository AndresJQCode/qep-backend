using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed record MembershipAcceptedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId) : IDomainEvent;
