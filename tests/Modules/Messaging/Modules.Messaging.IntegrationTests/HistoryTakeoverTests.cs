using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 §4.1, Review Focus 3 y regla R9: una conversación importada deja la regla de abiertas en cuanto la toca
/// el flujo en vivo — un entrante, una acción del asesor (resolver, reabrir), un saliente enviado desde QEP o una asignación. Desde
/// ahí, una parte del historial que llega después suma mensajes pero no le cambia el estado ni los no leídos.</summary>
public sealed class HistoryTakeoverTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001112233";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]}
        ]}
        """;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Webhooks, HttpClient Advisor, long ConnectedAt)
        : IDisposable
    {
        public void Dispose()
        {
            Webhooks.Dispose();
            Advisor.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        var connectedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, history_state, attempts, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, to_timestamp(@connectedAt), 'Requested', 'Requested', 0, now(), now(), 1)
            """,
            ("connectionId", connectionId), ("tenantId", tenant.TenantId), ("connectedAt", connectedAt)));
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        return new Fixture(factory, connectionString, tenant, factory.CreateClient(),
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), connectedAt);
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Webhooks, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static string History(int progress, params (string Wamid, string Body, long Timestamp, bool FromBusiness)[] messages) =>
        MetaPayloads.History(PhoneNumberId, 0, 1, progress, (WaId, messages));

    private static long HoursBefore(Fixture f, int hours) => f.ConnectedAt - (hours * 3600L);

    private static Task<Guid> ConversationIdAsync(Fixture f) =>
        ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

    private static Task<long> VersionAsync(Fixture f) =>
        ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

    private static Task<string> RowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || unread_count || '|' || CASE WHEN history_managed THEN 'True' ELSE 'False' END FROM messaging.conversations");

    /// <summary>Una importada abierta: el último es del contacto, de hace una hora.</summary>
    private static async Task ImportOpenAsync(Fixture f)
    {
        await PostAsync(f, History(50,
            ("wamid.o", "Te confirmo mañana", HoursBefore(f, 3), true),
            ("wamid.i", "¿Y el precio?", HoursBefore(f, 1), false)));
        Assert.Equal("Open|1|True", await RowAsync(f));
    }

    [Fact]
    public async Task ALiveMessageWithBsuidAdoptsTheImportedConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, History(50, ("wamid.h", "Hola", HoursBefore(f, 30), false)));
        var imported = await ConversationIdAsync(f);

        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.1", WaId, "wamid.live", f.ConnectedAt + 60, "¿Siguen abiertos?"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(imported, await ConversationIdAsync(f));
        Assert.Equal("CO.1|True|False|Open", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT user_id || '|' || CASE WHEN last_inbound_at IS NOT NULL THEN 'True' ELSE 'False' END || '|'
                || CASE WHEN history_managed THEN 'True' ELSE 'False' END || '|' || status
              FROM messaging.conversations
            """));
        Assert.Equal("CO.1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT k.whatsapp_user_id FROM customers.customers k JOIN messaging.conversations c ON c.customer_id = k.id"));
    }

    [Fact]
    public async Task AfterALiveMessageTheImportNoLongerChangesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, History(50, ("wamid.h", "Hola", HoursBefore(f, 30), false)));
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.1", WaId, "wamid.live", f.ConnectedAt + 60, "¿Siguen abiertos?"));
        Assert.Equal("Open|1|False", await RowAsync(f));

        // Un saliente más nuevo que el entrante en vivo: la regla de la importación la cerraría.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.h2", "Ya te respondo", f.ConnectedAt + 120, true)])));

        Assert.Equal("Open|1|False", await RowAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.h2'"));
    }

    // Review Focus 3.
    [Fact]
    public async Task AnAdvisorActionStopsTheImportRule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ImportOpenAsync(f);
        var url = ConversationUrl(f.Tenant.TenantId, await ConversationIdAsync(f));

        var resolved = await SendAsync(f.Advisor, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{await VersionAsync(f)}\"");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        // Un entrante reciente que la regla abriría.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.i2", "¿Hola?", f.ConnectedAt - 600, false)])));

        Assert.Equal("Resolved|0|False", await RowAsync(f));
    }

    // R9: responder desde QEP es tomarla. La conversación ya es del asesor (sin autoasignación), así que lo único que la saca de la
    // regla es el saliente.
    [Fact]
    public async Task AnAdvisorReplyStopsTheImportRule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ImportOpenAsync(f);
        var conversationId = await ConversationIdAsync(f);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()",
            ("m", await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant)));
        Assert.Equal("Open|1|True", await RowAsync(f));
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.tpl"}]}""");

        var sent = await SendAsync(f.Advisor, HttpMethod.Post, $"{MessagesUrl(f.Tenant.TenantId, conversationId)}/template",
            new { clientId = Guid.CreateVersion7(), name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var afterReply = await RowAsync(f);
        Assert.EndsWith("|False", afterReply, StringComparison.Ordinal);

        // Una parte vieja: el último sigue siendo el saliente del asesor, y la regla la cerraría.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.old", "Buenas", HoursBefore(f, 50), false)])));

        Assert.Equal(afterReply, await RowAsync(f));
        Assert.StartsWith("Open|", afterReply, StringComparison.Ordinal);
    }

    // R9: tomarla a mano.
    [Fact]
    public async Task TakingItStopsTheImportRule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ImportOpenAsync(f);
        var url = ConversationUrl(f.Tenant.TenantId, await ConversationIdAsync(f));

        var taken = await SendAsync(f.Advisor, HttpMethod.Post, $"{url}/take", ifMatch: $"\"{await VersionAsync(f)}\"");
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal("Open|1|False", await RowAsync(f));

        // Un saliente posterior al último entrante: la regla la cerraría y la dejaría en 0.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.o2", "Ya te cuento", HoursBefore(f, 1) + 60, true)])));

        Assert.Equal("Open|1|False", await RowAsync(f));
    }

    // R9: la autoasignación al responder. Meta rechaza el envío, así que el saliente no la saca de la regla: sólo la asignación.
    [Fact]
    public async Task TheAutoAssignmentOfAReplyStopsTheImportRule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ImportOpenAsync(f);
        var conversationId = await ConversationIdAsync(f);
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var sent = await SendAsync(f.Advisor, HttpMethod.Post, $"{MessagesUrl(f.Tenant.TenantId, conversationId)}/template",
            new { clientId = Guid.CreateVersion7(), name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sent.StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE assigned_member_id IS NOT NULL"));
        Assert.Equal("Open|1|False", await RowAsync(f));

        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.o2", "Ya te cuento", HoursBefore(f, 1) + 60, true)])));

        Assert.Equal("Open|1|False", await RowAsync(f));
    }
}
