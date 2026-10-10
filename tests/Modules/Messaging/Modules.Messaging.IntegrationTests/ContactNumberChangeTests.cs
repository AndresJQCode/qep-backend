using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.3 (RF3): las dos señales, en cualquier orden y repetidas, re-keyean la conversación sin
/// perder historia, cambian el BSUID del cliente y dejan un solo ContactChangedNumber; con una conversación ya creada
/// con el BSUID nuevo no se fusionan (D-A7).</summary>
public sealed class ContactNumberChangeTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, factory.CreateClient());
    }

    private static async Task SendAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Anonymous, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<long> ChangeEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'ContactChangedNumber'");

    [Fact]
    public async Task AUserIdUpdateReKeysTheConversationAndTheCustomerKeepingTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.NEW", null, "wamid.2", 1760000100, "después"));

        Assert.Equal($"{conversationId}|CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT id::text || '|' || user_id FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString,
            "SELECT whatsapp_user_id FROM customers.customers WHERE id = (SELECT customer_id FROM messaging.conversations)"));
    }

    // Revisión de la Task 10: las dos órdenes. true = el mensaje de sistema primero; false = user_id_update primero.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothSignalsInAnyOrderAndRepeatedApplyOnce(bool systemMessageFirst)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));

        if (systemMessageFirst)
        {
            await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050));
            await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
            await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.NEW", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys2", 1760000060));
        }
        else
        {
            await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
            await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050));
            await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        }

        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT user_id FROM messaging.conversations"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        // El mensaje de sistema no es un mensaje del hilo.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
    }

    // Decisión del controlador (ronda 1): en un mismo change el cambio de número va antes que los entrantes, así que el
    // primer mensaje con el BSUID nuevo cae en la conversación movida y el evento queda antes que él en el hilo.
    [Fact]
    public async Task ASignalAndTheFirstMessageWithTheNewBsuidInTheSameChangeLandInTheMovedConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        await SendAsync(f, MetaPayloads.SameChange(
            MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050),
            MetaPayloads.Inbound("111", "CO.NEW", null, "wamid.2", 1760000100, "con el número nuevo")));

        Assert.Equal($"1|{conversationId}|CO.NEW", await ScalarAsync<string>(f.ConnectionString,
            "SELECT count(*) OVER ()::text || '|' || id::text || '|' || user_id FROM messaging.conversations"));
        Assert.Equal(conversationId, await ScalarAsync<Guid>(f.ConnectionString, "SELECT conversation_id FROM messaging.messages WHERE wamid = 'wamid.2'"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString,
            """
            SELECT (SELECT occurred_at FROM messaging.messages WHERE details->>'type' = 'ContactChangedNumber')
                 < (SELECT occurred_at FROM messaging.messages WHERE wamid = 'wamid.2')
            """));
    }

    // P7: el cambio de número es mantenimiento de identidad, como los statuses: se aplica con Paused o el módulo apagado.
    // La conversación se siembra sin webhook para que la ruta no quede en caché antes de pausar; el entrante de CO.OTHER
    // en el mismo lote prueba que el descarte sí estaba activo.
    [Theory]
    [InlineData("paused", "user-id-update")]
    [InlineData("paused", "system-message")]
    [InlineData("module-off", "user-id-update")]
    [InlineData("module-off", "system-message")]
    public async Task TheNumberChangeAppliesEvenWithTheConnectionPausedOrTheModuleOff(string scenario, string signal)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var connectionId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM integrations.connections");
        var conversationId = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, connectionId, "CO.OLD", null);
        await ExecuteAsync(f.ConnectionString, scenario == "paused"
            ? "UPDATE integrations.connections SET status = 'Paused'"
            : "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE module_key = 'messaging'");

        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OTHER", null, "wamid.other", 1760000010, "descartado"));
        await SendAsync(f, signal == "user-id-update"
            ? MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW")
            : MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050));

        Assert.Equal($"1|{conversationId}|CO.NEW", await ScalarAsync<string>(f.ConnectionString,
            "SELECT count(*) OVER ()::text || '|' || id::text || '|' || user_id FROM messaging.conversations"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
    }

    [Fact]
    public async Task WithoutPhoneNumberIdTheUpdateRoutesByTheWaba()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW", phoneNumberId: null));

        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT user_id FROM messaging.conversations"));
    }

    // D-A7 y P6: un mensaje con el BSUID nuevo llegó antes que la señal. No se fusionan; las dos llevan el evento, una vez.
    [Fact]
    public async Task AConversationAlreadyCreatedWithTheNewBsuidIsNotMerged()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.NEW", null, "wamid.2", 1760000100, "con el número nuevo"));

        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));

        Assert.Equal("CO.NEW,CO.OLD", await ScalarAsync<string>(f.ConnectionString, "SELECT string_agg(user_id, ',' ORDER BY user_id) FROM messaging.conversations"));
        Assert.Equal(2L, await ChangeEventsAsync(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString,
            "SELECT count(DISTINCT conversation_id) FROM messaging.messages WHERE details->>'type' = 'ContactChangedNumber' AND details ? 'linkedConversationId'"));
        // El BSUID nuevo ya era de otro cliente (el incompleto del segundo mensaje): el viejo no se toca.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.OLD'"));
    }
}
