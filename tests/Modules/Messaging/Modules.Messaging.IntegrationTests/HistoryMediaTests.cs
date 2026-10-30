using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 §1 y P-6: un <c>media_placeholder</c> del historial se muestra como pendiente (o no disponible si Meta ya
/// no guarda el archivo) y, si el medio llega después con el mismo wamid, se encola para copiarlo.</summary>
public sealed class HistoryMediaTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001112233";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Webhooks, HttpClient Advisor)
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
        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, history_state, attempts, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, now(), 'Requested', 'Requested', 0, now(), now(), 1)
            """,
            ("connectionId", connectionId), ("tenantId", tenant.TenantId)));
        return new Fixture(factory, connectionString, tenant, factory.CreateClient(),
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions));
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Webhooks, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static async Task<JsonElement> ImportedMessageAsync(Fixture f)
    {
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var thread = await f.Advisor.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, conversationId), TestContext.Current.CancellationToken);
        return thread.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kind").GetString() != "Event");
    }

    [Fact]
    public async Task APlaceholderShowsPendingThenTheLateMediaIsQueued()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var recent = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();

        await PostAsync(f, MetaPayloads.HistoryMedia(PhoneNumberId, 1, WaId, "wamid.W", recent, null));

        var pending = await ImportedMessageAsync(f);
        Assert.Equal("Unsupported", pending.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("media").ValueKind);
        Assert.Equal("Pending", pending.GetProperty("mediaState").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("unsupportedReason").ValueKind);
        var versionBefore = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        await PostAsync(f, MetaPayloads.HistoryMedia(PhoneNumberId, 2, WaId, "wamid.W", recent, "MID"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal("MID", await ScalarAsync<string>(f.ConnectionString, "SELECT meta_media_id FROM messaging.message_media"));
        Assert.True(await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations") > versionBefore);
        var attached = await ImportedMessageAsync(f);
        Assert.Equal("Image", attached.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, attached.GetProperty("mediaState").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, attached.GetProperty("media").ValueKind);

        // Un tercer envío del mismo wamid no duplica el medio.
        await PostAsync(f, MetaPayloads.HistoryMedia(PhoneNumberId, 3, WaId, "wamid.W", recent, "MID2"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_media"));
    }

    [Fact]
    public async Task AnOldPlaceholderIsUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await PostAsync(f, MetaPayloads.HistoryMedia(PhoneNumberId, 1, WaId, "wamid.W", DateTimeOffset.UtcNow.AddDays(-20).ToUnixTimeSeconds(), null));

        var message = await ImportedMessageAsync(f);
        Assert.Equal("Unavailable", message.GetProperty("mediaState").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("unsupportedReason").ValueKind);
    }
}
