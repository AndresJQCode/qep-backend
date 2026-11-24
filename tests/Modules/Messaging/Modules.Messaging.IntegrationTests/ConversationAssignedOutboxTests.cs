using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-22 §6, §12 and D-10: the assignment of a conversation writes <c>messaging.conversation.assigned.v1</c> in the
/// same transaction as the change, through both doors that write the event row (the handlers by EF, the ingestion by SQL).</summary>
public sealed class ConversationAssignedOutboxTests
{
    private const string AssignedEvent = "messaging.conversation.assigned.v1";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid Owner, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, await OwnerMembershipIdAsync(connectionString, tenant), factory.CreateClient());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<long> AssignedEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @name", ("name", AssignedEvent));

    private static Task<long> VersionAsync(Fixture f) => ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

    private static Task<HttpResponseMessage> TransferAsync(Fixture f, HttpClient client, Guid conversationId, Guid memberId, long version) =>
        SendAsync(client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, conversationId)}/transfer", new { memberId }, $"\"{version}\"");

    private static async Task<Guid> IngestFirstConversationAsync(Fixture f, long timestamp)
    {
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", timestamp, "hola"));
        return await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
    }

    [Fact]
    public async Task TransferWritesTheAssignedEventInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var conversationId = await IngestFirstConversationAsync(f, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        var (beatriz, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", "advisor");
        var (carlos, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Carlos", "advisor");
        using var owner = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var version = await VersionAsync(f);

        // Two transfers with the same version: one commits and the other loses the concurrency check. The loser must leave no event.
        var responses = await Task.WhenAll(
            TransferAsync(f, owner, conversationId, beatriz, version),
            TransferAsync(f, owner, conversationId, carlos, version));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(response => response.StatusCode).Order());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'Transferred'"));
        Assert.Equal(1L, await AssignedEventsAsync(f));
        var winner = await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations");
        Assert.Equal(
            $"{winner}|Transferred|{f.Owner}|{conversationId}|{f.Tenant.TenantId}|{conversationId}",
            await ScalarAsync<string>(
                f.ConnectionString,
                """
                SELECT (payload->>'memberId') || '|' || (payload->>'reason') || '|' || (payload->>'actorMemberId') || '|' || (payload->>'conversationId')
                    || '|' || (payload->>'tenantId') || '|' || correlation_id
                FROM platform.outbox_messages WHERE event_name = @name
                """,
                ("name", AssignedEvent)));
    }

    // A forced failure of the save itself: a trigger rejects the Transferred history row, so the whole transaction must roll back and
    // leave neither the new assignee nor the event (the 412 race above cannot tell a failed If-Match from a failed save).
    [Fact]
    public async Task AFailedSaveLeavesNoAssignedEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var conversationId = await IngestFirstConversationAsync(f, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        var (beatriz, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", "advisor");
        using var owner = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var version = await VersionAsync(f);
        var assigneeBefore = await ScalarAsync<string>(f.ConnectionString, "SELECT coalesce(assigned_member_id::text, '') FROM messaging.conversations");
        await ExecuteAsync(
            f.ConnectionString,
            """
            CREATE FUNCTION messaging.reject_transferred() RETURNS trigger AS $$
            BEGIN
                IF NEW.details->>'type' = 'Transferred' THEN
                    RAISE EXCEPTION 'forced failure';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_transferred BEFORE INSERT ON messaging.messages
                FOR EACH ROW EXECUTE FUNCTION messaging.reject_transferred();
            """);

        var response = await TransferAsync(f, owner, conversationId, beatriz, version);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0L, await AssignedEventsAsync(f));
        Assert.Equal(version, await VersionAsync(f));
        Assert.Equal(
            assigneeBefore,
            await ScalarAsync<string>(f.ConnectionString, "SELECT coalesce(assigned_member_id::text, '') FROM messaging.conversations"));
    }

    [Fact]
    public async Task ATransferToOneselfWritesNoEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var conversationId = await IngestFirstConversationAsync(f, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        using var owner = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await TransferAsync(f, owner, conversationId, f.Owner, await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, await AssignedEventsAsync(f));
    }

    // D-A3 of the conversations spec: the customer written by another connection is born assigned, with an Inherited event.
    [Fact]
    public async Task InheritedIngestionWritesTheAssignedEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "222");
        var (member, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", "advisor");
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "por ventas"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", member));

        await IngestAsync(f, MetaPayloads.Inbound("333", "CO.1", null, "wamid.2", 1760000100, "por soporte"));

        var inheritedConversation = await ScalarAsync<Guid>(
            f.ConnectionString, "SELECT conversation_id FROM messaging.messages WHERE details->>'type' = 'Inherited'");
        Assert.Equal(1L, await AssignedEventsAsync(f));
        Assert.Equal(
            $"{member}|Inherited||{inheritedConversation}",
            await ScalarAsync<string>(
                f.ConnectionString,
                """
                SELECT (payload->>'memberId') || '|' || (payload->>'reason') || '|' || coalesce(payload->>'actorMemberId', '') || '|' || (payload->>'conversationId')
                FROM platform.outbox_messages WHERE event_name = @name
                """,
                ("name", AssignedEvent)));
    }

    [Fact]
    public async Task ACustomerWithoutAnAssigneeWritesNoEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "222");
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "por ventas"));

        await IngestAsync(f, MetaPayloads.Inbound("333", "CO.1", null, "wamid.2", 1760000100, "por soporte"));

        Assert.Equal(0L, await AssignedEventsAsync(f));
    }

    // Spec §12, D-6: the bell only claims events younger than 24 h by the outbox time. A late redelivery from Meta carries an hour of the
    // past; the row says when it was written and the payload says when the fact happened.
    [Fact]
    public async Task ALateMetaRedeliveryStampsTheOutboxRowWithNow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "222");
        var (member, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", "advisor");
        var thirtyOneHoursAgo = DateTimeOffset.UtcNow.AddHours(-31).ToUnixTimeSeconds();
        var thirtyHoursAgo = DateTimeOffset.UtcNow.AddHours(-30).ToUnixTimeSeconds();
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", thirtyOneHoursAgo, "por ventas"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", member));

        await IngestAsync(f, MetaPayloads.Inbound("333", "CO.1", null, "wamid.2", thirtyHoursAgo, "por soporte"));

        Assert.Equal(1L, await AssignedEventsAsync(f));
        Assert.True(await ScalarAsync<bool>(
            f.ConnectionString,
            "SELECT now() - occurred_at < interval '5 minutes' FROM platform.outbox_messages WHERE event_name = @name",
            ("name", AssignedEvent)));
        Assert.True(await ScalarAsync<bool>(
            f.ConnectionString,
            "SELECT (payload->>'occurredAt')::timestamptz < now() - interval '29 hours' FROM platform.outbox_messages WHERE event_name = @name",
            ("name", AssignedEvent)));
    }
}
