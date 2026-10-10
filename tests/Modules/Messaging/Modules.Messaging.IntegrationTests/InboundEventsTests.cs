using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.1–§8.2, §8.6, §8.7: cliente incompleto o vinculado con su evento antes del mensaje, los
/// eventos no tocan contadores ni foto (RF8), la reapertura sin actor, la herencia del asignado (D-A3) y la cita entrante.</summary>
public sealed class InboundEventsTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, factory.CreateClient());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> EventsAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(string_agg(details->>'type', ',' ORDER BY occurred_at, id), '') FROM messaging.messages WHERE direction = 3");

    [Fact]
    public async Task ANewPersonGetsAnIncompleteCustomerAndACustomerCreatedEventJustBeforeTheMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.1", 1760000000, "hola", profileName: "Laura Pérez"));

        var customerId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations");
        Assert.Equal("Incomplete|Laura Pérez|CO.1349120865530274", await ScalarAsync<string>(f.ConnectionString,
            "SELECT completeness || '|' || name || '|' || whatsapp_user_id FROM customers.customers WHERE id = @id", ("id", customerId)));
        Assert.Equal("CustomerCreated", await EventsAsync(f));
        Assert.Equal(customerId.ToString(), await ScalarAsync<string>(f.ConnectionString, "SELECT details->>'customerId' FROM messaging.messages WHERE direction = 3"));
        // §8.7: el evento del cliente va 2 ms antes del mensaje (Reopened -3, cliente -2, Inherited -1).
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT (SELECT occurred_at FROM messaging.messages WHERE direction = 1) - occurred_at = interval '2 milliseconds' FROM messaging.messages WHERE direction = 3"));
    }

    [Fact]
    public async Task AnExistingCustomerByPhoneIsLinked()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.1", 1760000000, "hola"));

        Assert.Equal(customerId, await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations"));
        Assert.Equal("CustomerLinked", await EventsAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    // RF8: un evento no sube unread_count, no es la foto, no tiene search_vector y no se repite con un reenvío.
    [Fact]
    public async Task EventsDoNotTouchCountersNorTheSnapshotNorRepeatOnAResend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var json = MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola");

        await IngestAsync(f, json);
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000999, "hola"));

        Assert.Equal("1|1|hola", await ScalarAsync<string>(f.ConnectionString,
            "SELECT unread_count || '|' || last_message_direction || '|' || last_message_preview FROM messaging.conversations"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            "SELECT c.last_message_id = m.id FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id AND m.direction = 1"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT search_vector = ''::tsvector AND status = 2 AND wamid IS NULL FROM messaging.messages WHERE direction = 3"));
        Assert.Equal("CustomerCreated", await EventsAsync(f));
    }

    [Fact]
    public async Task AnInboundOnAResolvedConversationReopensItWithAReopenedEventWithoutActor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved'");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "otra vez"));

        Assert.Equal("CustomerCreated,Reopened", await EventsAsync(f));
        Assert.Equal("Open|", await ScalarAsync<string>(f.ConnectionString,
            "SELECT c.status || '|' || coalesce(m.details->>'actor', '') FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id WHERE m.details->>'type' = 'Reopened'"));
    }

    // D-A3: el cliente con conversación asignada en la conexión A escribe por la B → nace asignada, con Inherited.
    [Theory]
    [InlineData("advisor", true)]
    [InlineData("billing", false)]
    public async Task ANewConversationOfACustomerInheritsTheAssigneeOnlyIfTheyCanReply(string role, bool inherits)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "222");
        var (member, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Beatriz", role);
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "por ventas"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", member));

        await IngestAsync(f, MetaPayloads.Inbound("333", "CO.1", null, "wamid.2", 1760000100, "por soporte"));

        var assigned = await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(c.assigned_member_id::text, '') FROM messaging.conversations c JOIN messaging.messages m ON m.conversation_id = c.id WHERE m.wamid = 'wamid.2'");
        Assert.Equal(inherits ? member.ToString() : string.Empty, assigned);
        Assert.Equal(inherits ? 1L : 0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'Inherited' AND details->>'target' = @m", ("m", member.ToString())));
        // Mismo cliente en las dos: se encontró por BSUID (Existing, sin crear otro). La segunda conversación nace con
        // customer_id, así que lleva CustomerLinked (§8.2: la transición de NULL a un valor).
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(DISTINCT customer_id) FROM messaging.conversations"));
        // Spec 2026-10-10 §8.7: orden fijo, cada evento con su propio occurred_at (no depende de dos UUID v7 del mismo
        // milisegundo): primero el cliente y después el asignado que se heredó de ese cliente.
        var secondThread = await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT string_agg(m.details->>'type', ',' ORDER BY m.occurred_at) || '|' || count(DISTINCT m.occurred_at)
            FROM messaging.messages m JOIN messaging.messages w ON w.conversation_id = m.conversation_id AND w.wamid = 'wamid.2'
            WHERE m.direction = 3
            """);
        Assert.Equal(inherits ? "CustomerLinked,Inherited|2" : "CustomerLinked|1", secondThread);
    }

    [Fact]
    public async Task AnInboundQuotingOurOutboundResolvesItAndAnUnknownQuoteKeepsOnlyTheWamid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var outbound = await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "wamid.ours");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "sí, ese", quotedWamid: "wamid.ours"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "y este", quotedWamid: "wamid.desde-el-telefono"));

        Assert.Equal($"{outbound}|wamid.ours", await ScalarAsync<string>(f.ConnectionString,
            "SELECT reply_to_message_id::text || '|' || reply_to_wamid FROM messaging.messages WHERE wamid = 'wamid.2'"));
        Assert.Equal("|wamid.desde-el-telefono", await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(reply_to_message_id::text, '') || '|' || reply_to_wamid FROM messaging.messages WHERE wamid = 'wamid.3'"));
    }
}
