using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §9.4 (RF9): varias entregas del mismo BSUID nuevo procesadas a la vez por dos pasadas del
/// worker → un cliente, una conversación, todos los mensajes, cero errores.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class InboundRaceTests
{
    [Fact]
    public async Task ConcurrentDeliveriesOfANewBsuidCreateOneCustomerAndOneConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        const int Deliveries = 8;
        for (var index = 0; index < Deliveries; index++)
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.RACE", null, $"wamid.{index}", 1760000000 + index, $"mensaje {index}"));
        }

        await Task.WhenAll(DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory));

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM customers.customers WHERE whatsapp_user_id = 'CO.RACE'"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND last_error IS NULL"));
        // Un solo evento de cliente. Su etiqueta puede ser CustomerLinked si la entrega que encontró el cliente ya creado
        // gana el INSERT de la conversación (decisión del controlador: cosmético, aceptado).
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' IN ('CustomerCreated', 'CustomerLinked')"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM customers.customers"));
    }

    // Spec 2026-10-10 §9.5 (P5): una fila vieja (sin BSUID) con el teléfono, y entregas del mismo BSUID nuevo que llegan
    // unas con teléfono (adoptan la vieja) y otras sin él (crean por BSUID). Si la creación gana, la adopción choca con
    // IX_conversations_connection_user y se reintenta una vez: la segunda vuelta encuentra la nueva por BSUID.
    [Fact]
    public async Task AdoptionOfALegacyRowRacingCreationByBsuidEndsInOneConversationForTheBsuid()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        using var anonymous = factory.CreateClient();
        const int Deliveries = 8;
        for (var index = 0; index < Deliveries; index++)
        {
            var waId = index % 2 == 0 ? "573001234567" : null;
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.LEGACY", waId, $"wamid.{index}", 1760000000 + index, $"mensaje {index}"));
        }

        await Task.WhenAll(DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory), DrainDeliveriesAsync(factory));

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.conversations WHERE user_id = 'CO.LEGACY'"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString,
            "SELECT count(*) FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.direction = 1 AND c.user_id = 'CO.LEGACY'"));
        Assert.Equal((long)Deliveries, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND last_error IS NULL"));
    }
}
