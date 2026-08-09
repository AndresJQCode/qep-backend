using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// A suspended membership was returned to active by an administrator.
///
/// Its own event, not a reuse of the invitation one: what happened here is that somebody
/// undid a suspension, and an audit trail that cannot tell that apart from a fresh
/// invitation loses the only fact worth keeping.
/// </summary>
public sealed record MembershipReactivatedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId) : IDomainEvent;
