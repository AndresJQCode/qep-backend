using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §8.2 por HTTP y contra la base: un DM crea una conversación de canal Instagram atada a un
/// cliente nuevo, con nombre y usuario del perfil; la lista la trae con su canal y la filtra; los echo de la app quedan
/// como salientes sin autor; el visto marca leídos los salientes; los adjuntos, los compartidos, las reacciones, los
/// botones y los borrados caen en su <c>kind</c>; la firma, la cuenta desconocida y la conexión pausada descartan.</summary>
public sealed class InstagramInboundTests
{
    private const string AccountId = InstagramPayloads.DefaultAccountId;
    private const string UserId = InstagramPayloads.DefaultUserId;
    private const string MediaUrl = "https://lookaside.fbsbx.com/ig_messaging_cdn/?asset_id=1&signature=abc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>El almacén de prueba: un diccionario, para no tocar R2 (como <c>MediaApiTests</c>).</summary>
    private sealed class FakeMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Objects.TryGetValue(key, out var stored) ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    /// <summary><c>Host</c> es la fábrica con el almacén falso: todo request y todo worker van por él.</summary>
    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMediaStore Store, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Anonymous);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool enableModule = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(store)));
        var tenant = await RegisterTenantAsync(host);
        if (enableModule)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var connectionId = await SeedInstagramConnectionAsync(host, tenant.TenantId, "Instagram Ventas", AccountId);
        // El perfil de quien escribe (§6.2): nombre y usuario, por el token de la cuenta.
        factory.MetaHandler.Respond($@"^/v24\.0/{UserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        return new Fixture(factory, host, store, connectionString, tenant, connectionId, host.CreateClient());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(f.Anonymous, json)).StatusCode);
        await DrainDeliveriesAsync(f.Host);
    }

    [Fact]
    public async Task TheVerificationAnswersTheChallengeAndABadSignatureIs401()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var verify = await f.Anonymous.GetAsync($"{InstagramWebhookUrl}?hub.mode=subscribe&hub.verify_token={TestVerifyToken}&hub.challenge=4242", Ct);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal("4242", await verify.Content.ReadAsStringAsync(Ct));

        var unsigned = await PostInstagramWebhookAsync(f.Anonymous, InstagramPayloads.Text("m1", 1760000000, "hola"), "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        // Firmado con el secreto de WhatsApp: también vale (V-1, el verificador acepta cualquiera de los dos).
        var bytes = System.Text.Encoding.UTF8.GetBytes(InstagramPayloads.Text("m1", 1760000000, "hola"));
        Assert.Equal(HttpStatusCode.OK, (await PostInstagramWebhookAsync(f.Anonymous, InstagramPayloads.Text("m1", 1760000000, "hola"), Sign(bytes))).StatusCode);
    }

    [Fact]
    public async Task ADmCreatesAnInstagramConversationWithItsCustomerAndProfile()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, InstagramPayloads.Text("m1", 1760000000, "¿Tienen disponible?"));
        // Reenvío de Meta: nada se duplica.
        await IngestAsync(f, InstagramPayloads.Text("m1", 1760000001, "¿Tienen disponible?"));
        await IngestAsync(f, InstagramPayloads.Text("m2", 1760000100, "¿Y el precio?"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal($"Instagram|{UserId}||laura.perez|Laura Pérez|Open|2|¿Y el precio?|1760000100",
            await ScalarAsync<string>(f.ConnectionString,
                "SELECT channel || '|' || user_id || '|' || coalesce(wa_id, '') || '|' || username || '|' || profile_name || '|' || status || '|' || unread_count || '|' || last_message_preview || '|' || extract(epoch from last_inbound_at)::bigint FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1 AND kind = 1"));
        // El perfil se pidió una sola vez, con el token de la cuenta.
        var profile = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == $"/v24.0/{UserId}");
        Assert.Equal($"Bearer {SentinelInstagramAccessToken}", profile.Authorization);
        // Spec 2026-10-14 decisión 10: el cliente nace incompleto con el id y el usuario de Instagram, sin teléfono.
        Assert.Equal($"Laura Pérez|{UserId}|laura.perez|Incomplete|",
            await ScalarAsync<string>(f.ConnectionString,
                "SELECT name || '|' || instagram_user_id || '|' || instagram_username || '|' || completeness || '|' || coalesce(phone, '') FROM customers.customers WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE customer_id IS NOT NULL"));

        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var page = await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        var item = page.GetProperty("items")[0];
        Assert.Equal("Instagram", item.GetProperty("channel").GetString());
        Assert.Equal("Instagram Ventas", item.GetProperty("connectionName").GetString());
        Assert.Equal(UserId, item.GetProperty("contact").GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("contact").GetProperty("waId").ValueKind);
        Assert.Equal("laura.perez", item.GetProperty("contact").GetProperty("username").GetString());
        Assert.Equal("Laura Pérez", item.GetProperty("customer").GetProperty("name").GetString());
        Assert.False(item.GetProperty("customer").GetProperty("isComplete").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("customerWindowExpiresAt").ValueKind);
        // El filtro por canal (§5.2).
        var whatsAppOnly = await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId) + "?channel=WhatsApp", Ct);
        Assert.Equal(0, whatsAppOnly.GetProperty("items").GetArrayLength());
        var instagramOnly = await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId) + "?channel=Instagram", Ct);
        Assert.Equal(1, instagramOnly.GetProperty("items").GetArrayLength());
        var bad = await client.GetAsync(ConversationsUrl(f.Tenant.TenantId) + "?channel=Telegram", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        // Las conexiones de la bandeja dicen su canal (§5.2).
        var connections = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections", Ct);
        var connection = Assert.Single(connections.EnumerateArray());
        Assert.Equal("Instagram", connection.GetProperty("channel").GetString());
        Assert.Equal("origenbotanico", connection.GetProperty("username").GetString());
    }

    [Fact]
    public async Task AnEchoFromTheInstagramAppIsAnOutboundWithoutAuthorAndOurOwnSendIsNotDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, InstagramPayloads.Text("m1", 1760000000, "hola"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "m_ours", occurredAtUnix: 1760000050);

        await IngestAsync(f, InstagramPayloads.Echo("m_ours", 1760000050, "respuesta"));
        await IngestAsync(f, InstagramPayloads.Echo("m_app", 1760000200, "Respondido desde el celular"));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'm_ours'"));
        Assert.Equal("2|1|1||Respondido desde el celular", await ScalarAsync<string>(f.ConnectionString,
            "SELECT direction || '|' || kind || '|' || status || '|' || coalesce(sent_by_member_id::text, '') || '|' || text FROM messaging.messages WHERE wamid = 'm_app'"));
        // La foto de la lista se mueve al echo; el contador y la ventana no (no es un entrante).
        Assert.Equal("1|Respondido desde el celular|2|1760000000", await ScalarAsync<string>(f.ConnectionString,
            "SELECT unread_count || '|' || last_message_preview || '|' || last_message_direction || '|' || extract(epoch from last_inbound_at)::bigint FROM messaging.conversations"));
        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var thread = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, conversationId), Ct);
        var echo = thread.GetProperty("items").EnumerateArray().Single(message => message.GetProperty("text").GetString() == "Respondido desde el celular");
        Assert.Equal("Outbound", echo.GetProperty("direction").GetString());
        Assert.Equal(JsonValueKind.Null, echo.GetProperty("sentBy").ValueKind);
    }

    [Fact]
    public async Task ASeenEventMarksOurMessagesUpToThatOneAsRead()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, InstagramPayloads.Text("m1", 1760000000, "hola"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var first = await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "m_out1", occurredAtUnix: 1760000010);
        var second = await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "m_out2", occurredAtUnix: 1760000020);

        await IngestAsync(f, InstagramPayloads.Seen("m_out1", 1760000030));

        Assert.Equal("3", await ScalarAsync<string>(f.ConnectionString, "SELECT status::text FROM messaging.messages WHERE id = @id", ("id", first)));
        Assert.Equal("1", await ScalarAsync<string>(f.ConnectionString, "SELECT status::text FROM messaging.messages WHERE id = @id", ("id", second)));

        await IngestAsync(f, InstagramPayloads.Seen("m_out2", 1760000040));
        Assert.Equal("3", await ScalarAsync<string>(f.ConnectionString, "SELECT status::text FROM messaging.messages WHERE id = @id", ("id", second)));
        Assert.Equal("3", await ScalarAsync<string>(f.ConnectionString, "SELECT last_message_status::text FROM messaging.conversations"));
    }

    [Fact]
    public async Task AttachmentsSharesReactionsPostbacksAndDeletionsLandInTheirKind()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.RespondWith("^/ig_messaging_cdn/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") } },
        });

        // Hora real: la copia del medio se rinde pasados 7 días desde occurred_at (§8.6), como en MediaApiTests.
        var t = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        await IngestAsync(f, InstagramPayloads.Text("m1", t, "hola"));
        await IngestAsync(f, InstagramPayloads.Attachment("m2", t + 10, "image", MediaUrl));
        await IngestAsync(f, InstagramPayloads.Attachment("m3", t + 20, "ig_post", "https://www.instagram.com/p/abc/"));
        await IngestAsync(f, InstagramPayloads.Attachment("m4", t + 30, "file", MediaUrl + "&pdf=1"));
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        await SeedOutboundAsync(f.ConnectionString, conversationId, f.Tenant.TenantId, f.ConnectionId, "m_out", occurredAtUnix: t + 35);
        await IngestAsync(f, InstagramPayloads.Reaction("m_out", t + 40, "❤️"));
        await IngestAsync(f, InstagramPayloads.Postback("m5", t + 50, "Ver catálogo", "CATALOG"));
        await IngestAsync(f, InstagramPayloads.Deleted("m1", t + 60));
        await DrainMediaAsync(f.Host);

        Assert.Equal("2|image/jpeg|" + MediaUrl, await ScalarAsync<string>(f.ConnectionString,
            "SELECT m.kind || '|' || media.mime_type || '|' || media.source_url FROM messaging.messages m JOIN messaging.message_media media ON media.message_id = m.id WHERE m.wamid = 'm2'"));
        Assert.Equal("stored", await ScalarAsync<string>(f.ConnectionString,
            "SELECT CASE WHEN stored_at IS NULL THEN coalesce(last_error, 'pending:' || attempts) ELSE 'stored' END FROM messaging.message_media media JOIN messaging.messages m ON m.id = media.message_id WHERE m.wamid = 'm2'"));
        Assert.Equal("14|https://www.instagram.com/p/abc/", await ScalarAsync<string>(f.ConnectionString, "SELECT kind || '|' || text FROM messaging.messages WHERE wamid = 'm3'"));
        Assert.Equal("5", await ScalarAsync<string>(f.ConnectionString, "SELECT kind::text FROM messaging.messages WHERE wamid = 'm4'"));
        Assert.Equal("9|❤️|m_out", await ScalarAsync<string>(f.ConnectionString, "SELECT kind || '|' || text || '|' || reply_to_wamid FROM messaging.messages WHERE kind = 9"));
        Assert.Equal("10|Ver catálogo", await ScalarAsync<string>(f.ConnectionString, "SELECT kind || '|' || text FROM messaging.messages WHERE wamid = 'm5'"));
        Assert.Equal("true", await ScalarAsync<string>(f.ConnectionString, "SELECT (details->>'deleted') FROM messaging.messages WHERE wamid = 'm1'"));

        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var thread = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, conversationId), Ct);
        var items = thread.GetProperty("items").EnumerateArray().ToArray();
        var deleted = items.Single(message => message.GetProperty("text").GetString() == "hola");
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        var share = items.Single(message => message.GetProperty("kind").GetString() == "Share");
        Assert.Equal("https://www.instagram.com/p/abc/", share.GetProperty("text").GetString());
        Assert.False(share.GetProperty("deleted").GetBoolean());
        var image = items.Single(message => message.GetProperty("kind").GetString() == "Image");
        Assert.Equal("image/jpeg", image.GetProperty("media").GetProperty("mimeType").GetString());
        var served = await client.GetAsync(MediaUrl(f.Tenant.TenantId, image.GetProperty("id").GetGuid()), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(7, (await served.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task AnUnknownAccountAPausedConnectionAndTheModuleOffDiscardTheInbound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await IngestAsync(f, InstagramPayloads.Text("m1", 1760000000, "hola", accountId: "999"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));

        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused' WHERE id = @id", ("id", f.ConnectionId));
        await IngestAsync(f, InstagramPayloads.Text("m2", 1760000001, "hola"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL AND provider = 'instagram'"));
    }
}
