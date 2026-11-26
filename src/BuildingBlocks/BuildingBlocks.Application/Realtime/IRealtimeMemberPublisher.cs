namespace BuildingBlocks.Application.Realtime;

/// <summary>
/// Spec 2026-11-22 §9.1: a notice for one member of a tenant, on every pod and every tab they have open. The member never subscribes:
/// the hub joins each connection to its own member group from the connection's claims, so only that member can receive it. Same contract
/// as <see cref="IRealtimePublisher"/>: called after the commit, it never throws, and the payload carries ids, not data.
/// </summary>
public interface IRealtimeMemberPublisher
{
    Task PublishToMemberAsync(Guid tenantId, Guid memberId, string kind, object payload, CancellationToken cancellationToken);
}
