using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 §4.4 y §8: los ecos (<c>smb_message_echoes</c>, lo que el negocio escribe desde la app) y los contactos
/// (<c>smb_app_state_sync</c>) entran también con la conexión pausada (D-5), sin tocar la ventana ni los no leídos.</summary>
public sealed class CoexistenceEchoAndContactsTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001112233";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, HttpClient Client, long ConnectedAt) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool paused = false)
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
        if (paused)
        {
            await ExecuteAsync(connectionString, "UPDATE integrations.connections SET status = 'Paused'");
        }

        return new Fixture(factory, connectionString, tenant.TenantId, factory.CreateClient(), connectedAt);
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static long HoursBefore(Fixture f, int hours) => f.ConnectedAt - (hours * 3600L);

    private static Task<string> RowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || unread_count || '|' || CASE WHEN history_managed THEN 'True' ELSE 'False' END FROM messaging.conversations");

    [Fact]
    public async Task AnEchoIsAnOutboundSentMessageThatDoesNotTouchTheWindow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.1", WaId, "wamid.live", f.ConnectedAt + 60, "Hola"));
        var before = await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_inbound_at::text || '|' || unread_count FROM messaging.conversations");

        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, WaId, "wamid.echo", "Respondido desde el celular", f.ConnectedAt + 120));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal("2|1|wamid.echo", await ScalarAsync<string>(f.ConnectionString,
            "SELECT direction || '|' || status || '|' || wamid FROM messaging.messages WHERE wamid = 'wamid.echo'"));
        Assert.Equal("2", await ScalarAsync<string>(f.ConnectionString, "SELECT last_message_direction::text FROM messaging.conversations"));
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_inbound_at::text || '|' || unread_count FROM messaging.conversations"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.automation_inbox WHERE message_id IN (SELECT id FROM messaging.messages WHERE wamid = 'wamid.echo')"));
    }

    [Fact]
    public async Task AnEchoToAnUnknownPhoneCreatesAnOpenLegacyConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, WaId, "wamid.echo", "Hola, te escribo desde el celular", f.ConnectedAt + 60));

        Assert.Equal("Open|0|False|True", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT status || '|' || unread_count || '|' || CASE WHEN history_managed THEN 'True' ELSE 'False' END || '|'
                || CASE WHEN user_id IS NULL THEN 'True' ELSE 'False' END
              FROM messaging.conversations WHERE wa_id = @waId
            """, ("waId", WaId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.echo' AND direction = 2"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE last_inbound_at IS NOT NULL"));
    }

    // R11: responder desde la app es actividad en vivo; una parte vieja del historial que llega después no la cierra.
    [Fact]
    public async Task AnEchoStopsTheImportRuleOfAnImportedConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 1, 50, (WaId, [("wamid.i", "¿Y el precio?", HoursBefore(f, 1), false)])));
        Assert.Equal("Open|1|True", await RowAsync(f));
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, WaId, "wamid.echo", "Cuesta 10", f.ConnectedAt + 60));

        Assert.Equal("Open|1|False", await RowAsync(f));
        Assert.True(await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations") > version);

        // El último ya es el eco del negocio: la regla de la importación la cerraría, pero ya no aplica.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 2, 60, (WaId, [("wamid.old", "Buenas", HoursBefore(f, 50), false)])));

        Assert.Equal("Open|1|False", await RowAsync(f));
    }

    [Fact]
    public async Task AContactCreatesAnIncompleteCustomerWithoutBsuid()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.ContactsSync(PhoneNumberId, "+" + WaId, "Ana Pérez", "Ana"));

        Assert.Equal("Incomplete|Ana Pérez|+573001112233|True", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT completeness || '|' || name || '|' || phone_e164 || '|' || CASE WHEN whatsapp_user_id IS NULL THEN 'True' ELSE 'False' END
              FROM customers.customers WHERE tenant_id = @tenantId
            """, ("tenantId", f.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    // D-7.
    [Fact]
    public async Task ARemovedContactLeavesTheCustomerUntouched()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.ContactsSync(PhoneNumberId, "+" + WaId, "Ana Pérez", "Ana"));
        var before = await ScalarAsync<string>(f.ConnectionString,
            "SELECT name || '|' || version FROM customers.customers WHERE tenant_id = @tenantId", ("tenantId", f.TenantId));

        await PostAsync(f, MetaPayloads.ContactsSync(PhoneNumberId, "+" + WaId, "Otro Nombre", "Otro", remove: true));
        await PostAsync(f, MetaPayloads.ContactsSync(PhoneNumberId, "+573009998877", "Quitado", "Quitado", remove: true));

        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString,
            "SELECT name || '|' || version FROM customers.customers WHERE tenant_id = @tenantId", ("tenantId", f.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers"));
    }

    // D-5.
    [Fact]
    public async Task EchoesAndContactsAreProcessedWithThePausedConnection()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, paused: true);

        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, WaId, "wamid.echo", "Desde el celular", f.ConnectedAt + 60));
        await PostAsync(f, MetaPayloads.ContactsSync(PhoneNumberId, "+573009998877", "Luis Gómez", "Luis"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.echo'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE phone_e164 = '+573009998877'"));
    }
}
