using System.Text.Json;
using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bootstrapper.Realtime;

/// <summary>
/// Plan N7: the fan-out between pods is <c>pg_notify('qep_realtime', …)</c>, like Messaging's. It opens a pooled connection of
/// <c>ConnectionStrings:QepDatabase</c> per notice, outside any transaction: callers publish after their commit, so Postgres delivers
/// at once, and <c>pg_notify</c> does go through PgBouncer (only <c>LISTEN</c> does not). It never throws: a failure is logged without
/// the payload and the write that caused it stands; the screen still rereads over HTTP.
/// </summary>
internal sealed partial class PgRealtimePublisher(IConfiguration configuration, ILogger<PgRealtimePublisher> logger)
    : IRealtimePublisher, IRealtimeMemberPublisher
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime notice {Kind} on a topic of tenant {TenantId} could not be published.")]
    private static partial void LogPublishFailed(ILogger logger, string kind, Guid tenantId, Exception exception);

    public Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken) =>
        NotifyAsync(tenantId, topic, memberId: null, kind, payload, cancellationToken);

    /// <summary>Spec 2026-11-22 §9.1: the same <c>pg_notify</c>, with the member in the envelope; the dispatcher of every pod sends it to
    /// that member's group only.</summary>
    public Task PublishToMemberAsync(Guid tenantId, Guid memberId, string kind, object payload, CancellationToken cancellationToken) =>
        NotifyAsync(tenantId, RealtimeTopics.MemberTopic, memberId, kind, payload, cancellationToken);

    private async Task NotifyAsync(
        Guid tenantId, string topic, Guid? memberId, string kind, object payload, CancellationToken cancellationToken)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(payload, payload.GetType(), RealtimeEventJson.Options);
            var notice = RealtimeEventJson.Serialize(new RealtimeEnvelope(kind, tenantId, topic, element, memberId));
            var connectionString = configuration.GetConnectionString("QepDatabase")
                ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
            command.Parameters.AddWithValue("channel", RealtimeEventJson.Channel);
            command.Parameters.AddWithValue("payload", notice);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Cancellation included: a notice is never worth failing a write that already committed.
            LogPublishFailed(logger, kind, tenantId, exception);
        }
    }
}
