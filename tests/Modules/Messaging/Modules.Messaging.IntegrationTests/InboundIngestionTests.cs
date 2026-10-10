using System.Net;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7.5 y §8.2: la ingesta crea la conversación, reabre una resuelta, sube el
/// contador, deduplica por wamid, no reabre ni suma con un reenvío viejo, deja la foto del último mensaje
/// aunque lleguen fuera de orden, descarta con Paused, módulo apagado y ruta desconocida, y no se traba
/// con una entrega rota.</summary>
public sealed class InboundIngestionTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string phoneNumberId = "111", bool enableModule = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        if (enableModule)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", phoneNumberId, "222");
        return new Fixture(factory, connectionString, tenant.TenantId, connectionId, factory.CreateClient());
    }

    [Fact]
    public async Task AnInboundTextCreatesTheConversationAndTheMessageOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var json = MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "¿Tienen disponible?");

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
        // Reenvío con otra hora (bytes distintos): otra entrega, mismo wamid.
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000999, "¿Tienen disponible?"))).StatusCode);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        // Spec 2026-10-10 §8.7: además del mensaje está su evento CustomerCreated (direction = 3); el reenvío no repite ninguno.
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 3"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
        var row = await ScalarAsync<string>(f.ConnectionString,
            "SELECT tenant_id::text || '|' || wa_id || '|' || profile_name || '|' || status || '|' || unread_count || '|' || last_message_preview || '|' || last_message_direction || '|' || last_message_status || '|' || version || '|' || extract(epoch from last_inbound_at)::bigint || '|' || last_inbound_wamid FROM messaging.conversations");
        Assert.Equal($"{f.TenantId}|573001234567|Laura|Open|1|¿Tienen disponible?|1|2|2|1760000000|wamid.1", row);
        Assert.Equal($"{f.TenantId}|1|1|2", await ScalarAsync<string>(f.ConnectionString, "SELECT tenant_id::text || '|' || direction || '|' || kind || '|' || status FROM messaging.messages WHERE direction = 1"));
    }

    [Fact]
    public async Task ANewInboundReopensAResolvedConversationButAnOldResendDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola"));
        await DrainDeliveriesAsync(f.Factory);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', unread_count = 0, version = version + 1");

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000001, "hola"));
        await DrainDeliveriesAsync(f.Factory);
        Assert.Equal("Resolved|0", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || unread_count FROM messaging.conversations"));

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.2", 1760000100, "otra"));
        await DrainDeliveriesAsync(f.Factory);
        Assert.Equal("Open|1|otra", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || unread_count || '|' || last_message_preview FROM messaging.conversations"));
    }

    // Meta puede reenviar tarde: un mensaje más viejo no pisa la foto del último.
    [Fact]
    public async Task OutOfOrderMessagesKeepTheNewestSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.new", 1760000200, "nuevo"));
        await DrainDeliveriesAsync(f.Factory);
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText("111", "573001234567", "wamid.old", 1760000100, "viejo"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("nuevo|2|1760000200|wamid.new", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_preview || '|' || unread_count || '|' || extract(epoch from last_activity_at)::bigint || '|' || last_inbound_wamid FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnImageCreatesTheMediaRowWithTheCaptionOnTheMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, MetaPayloads.InboundMedia("111", "573001234567", "wamid.img", 1760000000, "image", "media-1", "image/jpeg", caption: "la foto"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("2|la foto|", await ScalarAsync<string>(f.ConnectionString, "SELECT kind || '|' || caption || '|' || coalesce(text, '') FROM messaging.messages"));
        Assert.Equal("media-1|image/jpeg|0", await ScalarAsync<string>(f.ConnectionString, "SELECT meta_media_id || '|' || mime_type || '|' || attempts FROM messaging.message_media WHERE stored_at IS NULL"));
        Assert.Equal("la foto", await ScalarAsync<string>(f.ConnectionString, "SELECT last_message_preview FROM messaging.conversations"));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("module-off")]
    [InlineData("unknown-route")]
    public async Task PausedModuleOffAndUnknownRoutesDiscardTheInboundAndFinishTheDelivery(string scenario)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, enableModule: scenario != "module-off");
        using var _ = f.Factory;
        if (scenario == "paused")
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        }

        var phoneNumberId = scenario == "unknown-route" ? "000" : "111";
        await PostWebhookAsync(f.Client, MetaPayloads.InboundText(phoneNumberId, "573001234567", "wamid.1", 1760000000, "hola"));
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }

    // Review Focus 1.
    [Theory]
    [InlineData("""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"from":"573001234567","id":"w","timestamp":"abc","type":"text","text":{"body":"x"}}]}}]}]}""")]
    [InlineData("""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"history","value":{}}]}]}""")]
    [InlineData("""{"object":"whatsapp_business_account"}""")]
    public async Task AMalformedDeliveryIsProcessedWithoutRetries(string json)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostWebhookAsync(f.Client, json);
        await DrainDeliveriesAsync(f.Factory);

        Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, "SELECT attempts || '|' || CASE WHEN processed_at IS NOT NULL THEN 't' ELSE 'f' END FROM messaging.webhook_deliveries"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }
}
