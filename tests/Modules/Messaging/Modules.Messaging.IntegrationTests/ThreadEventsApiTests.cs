using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1, §8.6, §8.7 (RF8): el evento aparece en el hilo antes del mensaje que lo causó, con su
/// forma; hasMore lo cuenta; la búsqueda del historial no lo encuentra; replyTo en el hilo y en la búsqueda.</summary>
public sealed class ThreadEventsApiTests
{
    [Fact]
    public async Task EventsAndQuotesHaveTheirShapeInTheThreadAndTheSearch()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "hola pedido"));
        await DrainDeliveriesAsync(factory);
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        var outbound = await SeedOutboundAsync(connectionString, conversationId, tenant.TenantId, connectionId, "wamid.ours", occurredAtUnix: 1760000050);
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", 1760000100, "ese pedido", quotedWamid: "wamid.ours"));
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "y otro pedido", quotedWamid: "wamid.nadie"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var thread = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), TestContext.Current.CancellationToken);
        var oldest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=4", TestContext.Current.CancellationToken);
        var search = await client.GetFromJsonAsync<JsonElement>($"{SearchUrl(tenant.TenantId)}?q=pedido", TestContext.Current.CancellationToken);

        var items = thread.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        var created = items[0];
        Assert.Equal(("System", "Event", "Delivered", "CustomerCreated"),
            (created.GetProperty("direction").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("status").GetString(),
             created.GetProperty("event").GetProperty("type").GetString()));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("event").GetProperty("actor").ValueKind);
        foreach (var field in new[] { "text", "media", "location", "failureReason", "sentBy", "clientId", "replyTo" })
        {
            Assert.Equal(JsonValueKind.Null, created.GetProperty(field).ValueKind);
        }

        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("event").ValueKind);
        var quoted = items[3].GetProperty("replyTo");
        Assert.Equal((outbound, "Outbound", "Text", "respuesta"),
            (quoted.GetProperty("id").GetGuid(), quoted.GetProperty("direction").GetString(), quoted.GetProperty("kind").GetString(), quoted.GetProperty("preview").GetString()));
        Assert.Equal(JsonValueKind.Null, items[4].GetProperty("replyTo").ValueKind);
        Assert.True(oldest.GetProperty("hasMore").GetBoolean());
        var hits = search.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, hits.Length);
        Assert.All(hits, hit => Assert.Equal("Inbound", hit.GetProperty("direction").GetString()));
        Assert.Equal(outbound, hits.Single(hit => hit.GetProperty("text").GetString() == "ese pedido").GetProperty("replyTo").GetProperty("id").GetGuid());
    }
}
