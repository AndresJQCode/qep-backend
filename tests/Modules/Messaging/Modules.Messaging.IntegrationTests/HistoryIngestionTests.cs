using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 §4.1 y §4.4: el historial de la app entra como conversaciones legadas sin trabajo (sin no leídos de la
/// ingesta en vivo, sin ventana, sin bot, sin eventos), con la regla de abiertas sobre lo importado, y avanza el estado de la
/// sincronización.</summary>
public sealed class HistoryIngestionTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001112233";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid OwnerUserId, Guid ConnectionId, HttpClient Client, long ConnectedAt)
        : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool enableModule = true, bool withSync = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        if (enableModule)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        var connectedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!withSync)
        {
            return new Fixture(factory, connectionString, tenant.TenantId, tenant.OwnerUserId, connectionId, factory.CreateClient(), connectedAt);
        }

        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, history_state, attempts, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, to_timestamp(@connectedAt), 'Requested', 'Requested', 0, now(), now(), 1)
            """,
            ("connectionId", connectionId), ("tenantId", tenant.TenantId), ("connectedAt", connectedAt)));
        return new Fixture(factory, connectionString, tenant.TenantId, tenant.OwnerUserId, connectionId, factory.CreateClient(), connectedAt);
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static string History(int progress, params (string Wamid, string Body, long Timestamp, bool FromBusiness)[] messages) =>
        MetaPayloads.History(PhoneNumberId, 0, 1, progress, (WaId, messages));

    /// <summary>Otro hash de cuerpo con el mismo contenido: la entrega no se descarta como repetida.</summary>
    private static string WithNonce(string json, string nonce)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["test_nonce"] = nonce;
        return node.ToJsonString();
    }

    private static Task<string> ConversationRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || unread_count FROM messaging.conversations WHERE wa_id = @waId",
            ("waId", WaId));

    private static long HoursBefore(Fixture f, int hours) => f.ConnectedAt - (hours * 3600L);

    [Fact]
    public async Task AnImportedThreadIsAClosedLegacyConversationWithoutWork()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, History(50,
            ("wamid.h2", "¿Ya salió el pedido?", HoursBefore(f, 30), false),
            ("wamid.h1", "Hola, buenos días", HoursBefore(f, 40), false),
            ("wamid.h3", "Sí, va en camino", HoursBefore(f, 29), true)));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal("null|573001112233|Resolved|0|null|True|WhatsApp", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT coalesce(user_id, 'null') || '|' || wa_id || '|' || status || '|' || unread_count || '|'
                || coalesce(last_inbound_at::text, 'null') || '|' || CASE WHEN history_managed THEN 'True' ELSE 'False' END || '|' || channel
              FROM messaging.conversations
            """));
        Assert.Equal("wamid.h1:1:2,wamid.h2:1:2,wamid.h3:2:1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(wamid || ':' || direction || ':' || status, ',' ORDER BY occurred_at) FROM messaging.messages"));
        Assert.Equal("Sí, va en camino|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_preview || '|' || last_message_direction FROM messaging.conversations"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.automation_inbox"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE assigned_member_id IS NOT NULL"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM messaging.conversations c
              JOIN customers.customers k ON k.id = c.customer_id
             WHERE k.tenant_id = @tenantId AND k.phone_e164 = '+573001112233'
            """,
            ("tenantId", f.TenantId)));
    }

    [Fact]
    public async Task LastFromTheContactWithin24HoursOpensWithItsUnread()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, History(50,
            ("wamid.o", "Le confirmo mañana", HoursBefore(f, 3), true),
            ("wamid.i1", "Gracias", HoursBefore(f, 2), false),
            ("wamid.i2", "¿Y el precio?", HoursBefore(f, 1), false)));

        Assert.Equal("Open|2", await ConversationRowAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE last_inbound_at IS NOT NULL"));
    }

    // R10: el timestamp de Meta es de segundos. Un entrante del mismo segundo que el último saliente, y que Meta manda después, es
    // posterior: cuenta como no leído.
    [Fact]
    public async Task AnInboundInTheSameSecondAsTheLastOutboundCountsAsUnread()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var second = HoursBefore(f, 1);

        await PostAsync(f, History(50,
            ("wamid.o", "¿Te sirve el martes?", second, true),
            ("wamid.i", "Sí", second, false)));

        Assert.Equal("Open|1", await ConversationRowAsync(f));
    }

    // Sin fila de sincronización no hay hora de conexión: la importada queda cerrada aunque el último sea un entrante reciente.
    [Fact]
    public async Task AThreadForAConnectionWithoutSyncIsClosedWithoutUnread()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, withSync: false);

        await PostAsync(f, History(50, ("wamid.i", "¿Me ayudas?", HoursBefore(f, 1), false)));

        Assert.Equal("Resolved|0", await ConversationRowAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.history_syncs"));
    }

    [Fact]
    public async Task LastFromTheBusinessStaysClosed()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, History(50,
            ("wamid.i", "¿Tienen talla M?", HoursBefore(f, 2), false),
            ("wamid.o", "Sí señora", HoursBefore(f, 1), true)));

        Assert.Equal("Resolved|0", await ConversationRowAsync(f));
    }

    [Fact]
    public async Task LastFromTheContactOlderThan24HoursStaysClosed()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, History(50, ("wamid.i", "¿Tienen talla M?", HoursBefore(f, 25), false)));

        Assert.Equal("Resolved|0", await ConversationRowAsync(f));
    }

    [Fact]
    public async Task ChunksOutOfOrderGiveTheSameResult()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.i", "¿Y el envío?", HoursBefore(f, 1), false)])));
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 1, 30, (WaId, [("wamid.o", "Ya lo despaché", HoursBefore(f, 5), true)])));

        Assert.Equal("Open|1", await ConversationRowAsync(f));
        Assert.Equal("¿Y el envío?|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_preview || '|' || last_message_direction FROM messaging.conversations"));
    }

    [Fact]
    public async Task ARepeatedChunkDoesNotDuplicateMessages()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var json = History(50, ("wamid.i", "Hola", HoursBefore(f, 1), false), ("wamid.o", "Hola, ¿en qué te ayudo?", HoursBefore(f, 2), true));

        await PostAsync(f, json);
        await PostAsync(f, WithNonce(json, "again"));

        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
        Assert.Equal("Open|1", await ConversationRowAsync(f));
    }

    // Review Focus 4: lo que ya entró en vivo (o por eco) no se duplica ni se pisa.
    [Fact]
    public async Task AMessageAlreadyIngestedIsNotDuplicatedOrOverwritten()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.InboundText(PhoneNumberId, WaId, "wamid.same", HoursBefore(f, 1), "el original"));

        await PostAsync(f, History(50, ("wamid.same", "otro texto", HoursBefore(f, 1), false)));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.same'"));
        Assert.Equal("el original", await ScalarAsync<string>(f.ConnectionString, "SELECT text FROM messaging.messages WHERE wamid = 'wamid.same'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal("Open|1|False", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || unread_count || '|' || CASE WHEN history_managed THEN 'True' ELSE 'False' END FROM messaging.conversations"));
    }

    [Fact]
    public async Task ProgressAndPhaseAdvanceAndOneHundredCompletes()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 1, 2, 40, (WaId, [("wamid.a", "uno", HoursBefore(f, 50), false)])));
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 1, 20, (WaId, [("wamid.b", "dos", HoursBefore(f, 60), false)])));
        Assert.Equal("1|40|null", await ScalarAsync<string>(f.ConnectionString,
            "SELECT history_phase || '|' || history_progress || '|' || coalesce(completed_at::text, 'null') FROM messaging.history_syncs"));

        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 2, 3, 100, (WaId, [("wamid.c", "tres", HoursBefore(f, 30), false)])));

        Assert.Equal("2|100|True", await ScalarAsync<string>(f.ConnectionString,
            "SELECT history_phase || '|' || history_progress || '|' || CASE WHEN completed_at IS NOT NULL THEN 'True' ELSE 'False' END FROM messaging.history_syncs"));
        Assert.Equal(HistorySyncStatus.Completed, await StatusAsync(f));
    }

    // Una parte sin fase ni progreso (valores desconocidos) no pisa los que ya se conocían.
    [Fact]
    public async Task AChunkWithoutPhaseOrProgressKeepsTheKnownOnes()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 1, 2, 40, (WaId, [("wamid.a", "uno", HoursBefore(f, 50), false)])));

        var node = JsonNode.Parse(MetaPayloads.History(PhoneNumberId, 1, 3, 0, (WaId, [("wamid.b", "dos", HoursBefore(f, 60), false)])))!;
        var metadata = node["entry"]![0]!["changes"]![0]!["value"]!["history"]![0]!["metadata"]!.AsObject();
        metadata.Remove("phase");
        metadata.Remove("progress");
        await PostAsync(f, node.ToJsonString());

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.b'"));
        Assert.Equal("1|40|null", await ScalarAsync<string>(f.ConnectionString,
            "SELECT history_phase || '|' || history_progress || '|' || coalesce(completed_at::text, 'null') FROM messaging.history_syncs"));
    }

    [Fact]
    public async Task ADeclinedHistoryMarksTheSyncDeclined()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.HistoryDeclined(PhoneNumberId));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.history_syncs WHERE declined_at IS NOT NULL"));
        Assert.Equal(HistorySyncStatus.Declined, await StatusAsync(f));
    }

    // Spec §8 y D-5: el historial no se puede volver a pedir, así que entra con la conexión pausada y el módulo apagado.
    [Fact]
    public async Task APausedConnectionStillImportsTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, enableModule: false);
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        await PostAsync(f, History(50, ("wamid.i", "Hola", HoursBefore(f, 30), false)));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.i'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.history_syncs WHERE history_progress = 50"));
    }

    [Fact]
    public async Task TheWindowStaysClosedOnAnOpenImportedConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, History(50, ("wamid.i", "¿Me ayudas?", HoursBefore(f, 1), false)));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        using var client = CreateClient(f.Factory, f.OwnerUserId, f.TenantId, ReadPermissions);

        var response = await client.GetAsync(ConversationUrl(f.TenantId, conversationId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Open", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("customerWindowExpiresAt").ValueKind);
    }

    // Regla R4: cada sentencia de la importación que cambia una conversación sube su versión, así que lo que un asesor cargó antes
    // falla por concurrencia en vez de pisarse en silencio.
    [Fact]
    public async Task AnAdvisorChangeLoadedBeforeAChunkFailsOptimistically()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, History(50, ("wamid.o", "Te escribo luego", HoursBefore(f, 40), true)));

        using var scope = f.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var conversation = await dbContext.Conversations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConversationStatus.Resolved, conversation.Status);

        await PostAsync(f, History(60, ("wamid.o2", "¿Sigues interesada?", HoursBefore(f, 30), true)));

        conversation.Reopen(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<HistorySyncStatus> StatusAsync(Fixture f)
    {
        using var scope = f.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var sync = await dbContext.HistorySyncs.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        return sync.StatusAt(DateTimeOffset.UtcNow);
    }
}
