using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed record MembershipInvitedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId,
    DateTimeOffset ExpiresAt) : IDomainEvent;
