using System.Collections.Concurrent;
using System.Text.Json;
using BuildingBlocks.Application.Realtime;

namespace Modules.Support.IntegrationTests;

/// <summary>Stands in for <see cref="IRealtimePublisher"/> (plan Task 2): records every notice in order. With <see cref="Fail"/> it throws,
/// to prove that a broken publisher never fails the write that announced.</summary>
internal sealed class RecordingRealtimePublisher : IRealtimePublisher
{
    private readonly ConcurrentQueue<Notice> _notices = new();

    public bool Fail { get; set; }

    public IReadOnlyList<Notice> Notices => [.. _notices];

    public Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("The realtime publisher is down.");
        }

        _notices.Enqueue(new Notice(tenantId, topic, kind, JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonSerializerOptions.Web)));
        return Task.CompletedTask;
    }

    /// <summary>The notices of one tenant, in the order they were published.</summary>
    public IReadOnlyList<Notice> Of(Guid tenantId) => [.. _notices.Where(notice => notice.TenantId == tenantId)];

    internal sealed record Notice(Guid TenantId, string Topic, string Kind, JsonElement Payload)
    {
        public Guid TicketId => Payload.GetProperty("ticketId").GetGuid();
    }
}
