using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.1: la clave es el BSUID (RF1), una fila vieja se adopta por teléfono (RF2), un número
/// reciclado con otro BSUID es otra conversación, y el teléfono o el usuario que llegan después se completan.</summary>
public sealed class InboundBsuidTests
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

    [Fact]
    public async Task ABsuidOnlyInboundCreatesTheConversationWithoutPhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.1", 1760000000, "hola", username: "laura.p"));

        Assert.Equal("CO.1349120865530274||laura.p|Laura|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT user_id || '|' || coalesce(wa_id, '') || '|' || username || '|' || profile_name || '|' || unread_count FROM messaging.conversations"));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var contact = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), TestContext.Current.CancellationToken))
            .GetProperty("items")[0].GetProperty("contact");
        Assert.Equal("CO.1349120865530274", contact.GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, contact.GetProperty("waId").ValueKind);
        Assert.Equal("laura.p", contact.GetProperty("username").GetString());
    }

    [Fact]
    public async Task TheFirstBsuidInboundAdoptsTheLegacyPhoneConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var legacy = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", "573001234567", "wamid.1", 1760000000, "hola"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1349120865530274", null, "wamid.2", 1760000100, "otra"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal($"{legacy}|CO.1349120865530274|573001234567|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT id::text || '|' || user_id || '|' || wa_id || '|' || unread_count FROM messaging.conversations"));
    }

    // D-A7 y §7.1: una conversación con BSUID puede repetir teléfono (número reciclado por otra persona).
    [Fact]
    public async Task ARecycledNumberWithAnotherBsuidIsAnotherConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.AAA", "573001234567", "wamid.1", 1760000000, "soy A"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.BBB", "573001234567", "wamid.2", 1760000100, "soy B"));

        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE wa_id = '573001234567'"));
    }

    [Fact]
    public async Task ThePhoneAndUsernameThatArriveLaterAreFilledWithoutLosingThem()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", 1760000000, "a", username: "laura.p"));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.2", 1760000100, "b", username: null));
        await IngestAsync(f, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", 1760000200, "c", username: null));

        Assert.Equal("573001234567|laura.p|3", await ScalarAsync<string>(f.ConnectionString,
            "SELECT wa_id || '|' || username || '|' || unread_count FROM messaging.conversations"));
    }

    [Fact]
    public async Task AnInboundWithoutBsuidIsSkippedAndTheDeliveryIsDone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var json = MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.1", 1760000000, "hola");
        Assert.Contains("\"from_user_id\":\"CO.1\",", json, StringComparison.Ordinal);
        var withoutBsuid = json.Replace("\"from_user_id\":\"CO.1\",", string.Empty, StringComparison.Ordinal);

        await IngestAsync(f, withoutBsuid);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND last_error IS NULL"));
    }
}
