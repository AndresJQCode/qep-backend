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

    [Fact]
    public async Task BothSignalsInAnyOrderAndRepeatedApplyOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SendAsync(f, MetaPayloads.Inbound("111", "CO.OLD", null, "wamid.1", 1760000000, "antes"));

        await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.OLD", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys", 1760000050));
        await SendAsync(f, MetaPayloads.UserIdUpdate("CO.OLD", "CO.NEW"));
        await SendAsync(f, MetaPayloads.UserChangedUserId("111", "CO.NEW", "CO.NEW", "User Laura changed from CO.OLD to CO.NEW", "wamid.sys2", 1760000060));

        Assert.Equal("CO.NEW", await ScalarAsync<string>(f.ConnectionString, "SELECT user_id FROM messaging.conversations"));
        Assert.Equal(1L, await ChangeEventsAsync(f));
        // El mensaje de sistema no es un mensaje del hilo.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
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
