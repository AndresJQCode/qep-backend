using System.Collections.Concurrent;
using System.Text.Json;
using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Notifications.Infrastructure.InApp;
using Npgsql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22: shared helpers of the in-app inbox tests. The host runs the real migrations, so (as in
/// <see cref="NotificationsDeliveryHarness"/>) the raw-SQL helpers only work once the host has started.</summary>
internal static class InAppTestSupport
{
    public static NotificationsApiFactory NewFactory(string connectionString, RecordingMemberPublisher publisher) =>
        new(
            connectionString,
            configureServices: services =>
            {
                services.RemoveAll<IRealtimeMemberPublisher>();
                services.AddSingleton<IRealtimeMemberPublisher>(publisher);
            });

    /// <summary>One drain tick of the consumer, the way the loop would run it.</summary>
    public static Task<bool> DrainAsync(NotificationsApiFactory factory, string consumer)
    {
        var definition = factory.Services.GetServices<InAppOutboxConsumer>().Single(candidate => candidate.ConsumerName == consumer);
        return definition.DrainAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<Guid> MemberIdOfAsync(string connectionString, Guid tenantId, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenant AND user_id = @user", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The development stub authenticates by headers; the member is whoever has an active membership for that user.</summary>
    public static HttpClient ClientFor(NotificationsApiFactory factory, Guid tenantId, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        return client;
    }

    /// <summary>A second member of the tenant, written directly (no invitation), named so a name lookup has something to find.</summary>
    public static async Task<(Guid MemberId, Guid UserId)> SeedMemberAsync(string connectionString, Guid tenantId, string displayName)
    {
        var memberId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            VALUES (@id, @user, @tenant, 'Active', ARRAY['advisor']::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), @name)
            """,
            connection);
        command.Parameters.AddWithValue("id", memberId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("name", displayName);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return (memberId, userId);
    }

    /// <summary>An entry written the way the consumers leave it, with a chosen age and read state.</summary>
    public static async Task<Guid> InsertEntryAsync(
        string connectionString,
        Guid tenantId,
        Guid memberId,
        string kind,
        string dataJson,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt = null,
        Guid? resourceId = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.in_app_notifications (id, tenant_id, member_id, kind, resource_id, data, dedupe_key, created_at, read_at)
            VALUES (@id, @tenant, @member, @kind, @resource, CAST(@data AS jsonb), @key, @created, @read)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("member", memberId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("resource", (object?)resourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("data", dataJson);
        command.Parameters.AddWithValue("key", id.ToString("D"));
        command.Parameters.AddWithValue("created", createdAt);
        command.Parameters.Add(new NpgsqlParameter("read", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)readAt ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task<IReadOnlyList<InAppRow>> EntriesAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, member_id, kind, resource_id, data::text, dedupe_key, source_message_id, created_at, read_at, resolved_at
            FROM notifications.in_app_notifications WHERE tenant_id = @tenant ORDER BY created_at, id
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<InAppRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new InAppRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                JsonDocument.Parse(reader.GetString(4)).RootElement,
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }
}

internal sealed record InAppRow(
    Guid Id,
    Guid MemberId,
    string Kind,
    Guid? ResourceId,
    JsonElement Data,
    string DedupeKey,
    Guid? SourceMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Records what would go to the member's group, and fails nothing: the real publisher never throws either.</summary>
internal sealed class RecordingMemberPublisher : IRealtimeMemberPublisher
{
    private readonly ConcurrentQueue<MemberNotice> _published = new();

    public IReadOnlyList<MemberNotice> Published => [.. _published];

    public Task PublishToMemberAsync(Guid tenantId, Guid memberId, string kind, object payload, CancellationToken cancellationToken)
    {
        _published.Enqueue(new MemberNotice(tenantId, memberId, kind, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
        return Task.CompletedTask;
    }
}

internal sealed record MemberNotice(Guid TenantId, Guid MemberId, string Kind, JsonElement Payload);
