using System.Text.Json;
using Modules.Notifications.Domain;
using Npgsql;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Task-notices spec 2026-11-22 §5.3, D-2 and D-7: the assignment events open bell entries and escalation rows; the release
/// event closes only the former assignee's entry.</summary>
public sealed class InAppSupportTaskTests
{
    private const string Consumer = "notifications.in-app.support";
    private const string TaskAssignedEvent = "support.ticket.task-assigned.v1";
    private const string TicketAssignedEvent = "support.ticket.assigned.v1";
    private const string ReleasedEvent = "support.ticket.assignee-released.v1";

    [Fact]
    public async Task TaskAssignedCreatesAnEntryAndOneEscalation()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (assigner, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        var ticketId = Guid.CreateVersion7();
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, ticketId, owner, assigner));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.SupportTaskAssigned), entry.Kind);
        Assert.Equal(ticketId, entry.ResourceId);
        Assert.Equal($"support-task-assigned:{ticketId:D}:{messageId:D}", entry.DedupeKey);
        Assert.Equal(assigner, entry.Data.GetProperty("actorMemberId").GetGuid());
        Assert.Equal("SOP-000127", entry.Data.GetProperty("ticketNumber").GetString());
        var escalation = Assert.Single(await EscalationsAsync(database.GetConnectionString(), tenantId));
        Assert.Equal("Pending", escalation.Status);
        Assert.Equal(entry.Id, escalation.InAppNotificationId);
        Assert.Equal(owner, escalation.MemberId);
        Assert.Equal(ticketId, escalation.TicketId);
        Assert.InRange((escalation.DueAt - entry.CreatedAt).TotalSeconds, 30 * 60 - 1, 30 * 60 + 1);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task ARetriedMessageAddsNoEntryNorEscalation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (assigner, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        await InsertOutboxAsync(
            database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, Guid.CreateVersion7(), owner, assigner));
        await DrainAsync(factory, Consumer);

        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE notifications.inbox_messages SET processed_at = NULL, claimed_until = NULL WHERE consumer = 'notifications.in-app.support'");
        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(await EscalationsAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task SelfAssignmentNotifiesNobody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        await InsertOutboxAsync(
            database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, Guid.CreateVersion7(), owner, owner));

        await DrainAsync(factory, Consumer);

        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Empty(await EscalationsAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task TwoAssignmentsOfTheSameTicketAreTwoEntries()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (assigner, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        var ticketId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, ticketId, owner, assigner), now.AddMinutes(-2));
        await InsertOutboxAsync(database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, ticketId, owner, assigner), now.AddMinutes(-1));

        await DrainAsync(factory, Consumer);

        Assert.Equal(2, (await EntriesAsync(database.GetConnectionString(), tenantId)).Count);
        Assert.Equal(2, (await EscalationsAsync(database.GetConnectionString(), tenantId)).Count);
    }

    // Review focus 1: A to B in one transaction publishes both events with the same occurred_at. Resolving by kind and resource alone
    // would also close B's brand new entry.
    [Fact]
    public async Task ReassigningResolvesOnlyTheFormerAssigneesEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (memberB, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        var ticketId = Guid.CreateVersion7();
        var entryOfA = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, nameof(InAppNotificationKind.SupportTaskAssigned),
            JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", resolution = (string?)null }),
            DateTimeOffset.UtcNow.AddMinutes(-5), resourceId: ticketId);
        var at = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(
            database.GetConnectionString(), ReleasedEvent, ReleasedPayload(tenantId, ticketId, owner, "Reassigned", isInternal: true), at);
        await InsertOutboxAsync(
            database.GetConnectionString(), TaskAssignedEvent, AssignedPayload(tenantId, ticketId, memberB, owner), at);

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        var former = entries.Single(entry => entry.Id == entryOfA);
        Assert.NotNull(former.ResolvedAt);
        Assert.NotNull(former.ReadAt);
        Assert.Equal("Reassigned", former.Data.GetProperty("resolution").GetString());
        var current = entries.Single(entry => entry.MemberId == memberB);
        Assert.Null(current.ResolvedAt);
        Assert.Null(current.ReadAt);
    }

    [Fact]
    public async Task ResolvingTheTaskResolvesTheEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var ticketId = Guid.CreateVersion7();
        var entry = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, nameof(InAppNotificationKind.SupportTaskAssigned),
            JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", resolution = (string?)null }),
            DateTimeOffset.UtcNow.AddMinutes(-5), resourceId: ticketId);
        await InsertOutboxAsync(
            database.GetConnectionString(), ReleasedEvent, ReleasedPayload(tenantId, ticketId, owner, "Resolved", isInternal: true));

        await DrainAsync(factory, Consumer);

        var row = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(entry, row.Id);
        Assert.NotNull(row.ResolvedAt);
        Assert.Equal("Resolved", row.Data.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task CustomerTicketAssignedCreatesATicketEntryAndOneEscalation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (assigner, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        var ticketId = Guid.CreateVersion7();
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), TicketAssignedEvent, AssignedPayload(tenantId, ticketId, owner, assigner));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(nameof(InAppNotificationKind.SupportTicketAssigned), entry.Kind);
        Assert.Equal($"support-ticket-assigned:{ticketId:D}:{messageId:D}", entry.DedupeKey);
        var escalation = Assert.Single(await EscalationsAsync(database.GetConnectionString(), tenantId));
        Assert.Equal("Pending", escalation.Status);
        Assert.Equal(entry.Id, escalation.InAppNotificationId);
    }

    [Fact]
    public async Task ReleasingACustomerTicketResolvesOnlyTheTicketEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var ticketId = Guid.CreateVersion7();
        var data = JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", resolution = (string?)null });
        var ticketEntry = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, nameof(InAppNotificationKind.SupportTicketAssigned), data,
            DateTimeOffset.UtcNow.AddMinutes(-5), resourceId: ticketId);
        var taskEntry = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, nameof(InAppNotificationKind.SupportTaskAssigned), data,
            DateTimeOffset.UtcNow.AddMinutes(-4), resourceId: ticketId);
        await InsertOutboxAsync(
            database.GetConnectionString(), ReleasedEvent, ReleasedPayload(tenantId, ticketId, owner, "Closed", isInternal: false));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.NotNull(entries.Single(entry => entry.Id == ticketEntry).ResolvedAt);
        Assert.Null(entries.Single(entry => entry.Id == taskEntry).ResolvedAt);
    }

    private static string AssignedPayload(Guid tenantId, Guid ticketId, Guid assignee, Guid assignedBy) =>
        JsonSerializer.Serialize(new
        {
            ticketId,
            tenantId,
            ticketNumber = "SOP-000127",
            assigneeMemberId = assignee,
            assignedByMemberId = assignedBy,
        });

    private static string ReleasedPayload(Guid tenantId, Guid ticketId, Guid former, string reason, bool isInternal) =>
        JsonSerializer.Serialize(new
        {
            ticketId,
            tenantId,
            ticketNumber = "SOP-000127",
            formerAssigneeMemberId = former,
            reason,
            isInternal,
        });

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<EscalationRow>> EscalationsAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT in_app_notification_id, ticket_id, member_id, status, due_at
            FROM notifications.task_notice_escalations WHERE tenant_id = @tenant ORDER BY created_at, id
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<EscalationRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new EscalationRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private sealed record EscalationRow(Guid InAppNotificationId, Guid TicketId, Guid MemberId, string Status, DateTimeOffset DueAt);
}
