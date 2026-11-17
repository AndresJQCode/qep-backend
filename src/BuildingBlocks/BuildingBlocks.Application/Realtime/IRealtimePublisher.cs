namespace BuildingBlocks.Application.Realtime;

/// <summary>
/// Spec 2026-11-15 §5.1: a notice for the subscribers of <paramref name="topic"/> in a tenant, fanned out to every pod. It is called
/// after the commit, and it never throws: a failure is logged and the write that caused it stands (the screen still has HTTP and
/// optimistic concurrency). The payload carries ids, not data: the hub announces, it does not transport.
/// </summary>
public interface IRealtimePublisher
{
    Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken);
}
