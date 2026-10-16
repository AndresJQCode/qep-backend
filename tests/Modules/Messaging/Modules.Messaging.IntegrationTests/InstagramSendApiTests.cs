using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §5.2 y §6.2 por HTTP: responder en una conversación de Instagram va a <c>graph.instagram.com</c> con el
/// IGSID y el token de la cuenta; el texto tiene tope de 1000; el adjunto y la nota de voz viajan como URL firmada de R2 (la nota
/// transcodificada a WAV); el leído manda <c>mark_seen</c>; plantillas y conversación nueva responden <c>channel_unsupported</c>.</summary>
public sealed class InstagramSendApiTests
{
    private const string AccountId = InstagramPayloads.DefaultAccountId;
    private const string UserId = InstagramPayloads.DefaultUserId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un solo diccionario para el staging y el R2 final, como en <c>SendMediaMessageApiTests</c>.</summary>
    private sealed class FakeBucket : IMessagingMediaStore, IMessagingUploadStaging
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public List<(string Key, TimeSpan Lifetime)> DownloadUrls { get; } = [];

        public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new MessagingUploadUrl(new Uri($"https://r2.test/{key}"), DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<StagedUpload?>(Objects.TryGetValue(key, out var stored) ? new StagedUpload(stored.Bytes.Length, "etag-" + stored.Bytes.Length) : null);

        public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken)
        {
            Objects[finalKey] = Objects[stagingKey];
            return Task.CompletedTask;
        }

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

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            DownloadUrls.Add((key, lifetime));
            return Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
        }
    }

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConnectionId, Guid ConversationId, Guid MemberId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var bucket = new FakeBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedInstagramConnectionAsync(host, tenant.TenantId, "Instagram Ventas", AccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{UserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using var anonymous = host.CreateClient();
        await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Fixture(factory, host, bucket, connectionString, tenant, connectionId, conversationId, memberId, client);
    }

    private static string SendPath => $"/v24.0/{AccountId}/messages";

    private static void ScriptSendOk(FakeMetaGraphHandler meta, string mid = "mid.out") =>
        meta.Respond($"^{SendPath}$", HttpStatusCode.OK, $$"""{"recipient_id":"{{UserId}}","message_id":"{{mid}}"}""");

    private static MetaRequest[] Sends(FakeMetaGraphHandler meta) =>
        meta.Requests.Where(request => request.Uri!.AbsolutePath == SendPath && request.Method == HttpMethod.Post).ToArray();

    [Fact]
    public async Task ATextReplyGoesToGraphInstagramWithTheIgsidAndTheAccountToken()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "¡Claro que sí!" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Text", "Sent", "¡Claro que sí!"), (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("text").GetString()));
        var send = Assert.Single(Sends(f.Factory.MetaHandler), request => !IsReadReceipt(request));
        Assert.Equal("graph.instagram.com", send.Uri!.Host);
        Assert.Equal($"Bearer {SentinelInstagramAccessToken}", send.Authorization);
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal(UserId, body.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("¡Claro que sí!", body.RootElement.GetProperty("message").GetProperty("text").GetString());
        Assert.False(body.RootElement.TryGetProperty("messaging_product", out var product));
        Assert.Equal(JsonValueKind.Undefined, product.ValueKind);
        // Base: saliente Sent con el mid, y la foto de la conversación sobre él.
        Assert.Equal("2|1|mid.out", await ScalarAsync<string>(f.ConnectionString,
            "SELECT direction || '|' || status || '|' || wamid FROM messaging.messages WHERE client_id = @c", ("c", clientId)));
        Assert.Equal("¡Claro que sí!", await ScalarAsync<string>(f.ConnectionString, "SELECT last_message_preview FROM messaging.conversations"));
    }

    [Fact]
    public async Task ATextOver1000CharactersIs422OnTheTextFieldBeforeAnythingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = new string('a', 1001) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("text", out var textError));
        Assert.NotEqual(JsonValueKind.Undefined, textError.ValueKind);
        Assert.Empty(Sends(f.Factory.MetaHandler));
        // Un request inválido no cambia estado: la conversación sigue sin asignar.
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE assigned_member_id IS NOT NULL"));
    }

    [Fact]
    public async Task AnOutsideWindowErrorFromInstagramIsWindowClosedAndTheRowIsFailedWithCode10()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond($"^{SendPath}$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(10, 2534022));
        var clientId = Guid.CreateVersion7();

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "hola" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.window_closed", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.Equal("4|10", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || failure_code FROM messaging.messages WHERE client_id = @c", ("c", clientId)));
    }

    [Fact]
    public async Task AnImageIsSentAsAnAttachmentWithASignedR2UrlWithoutUploadingToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler, "mid.media");
        var uploadId = Guid.CreateVersion7();
        var png = Png();
        f.Bucket.Objects[MessagingMediaKeys.Staging(f.Tenant.TenantId, f.ConversationId, f.MemberId, uploadId, ".png")] = (png, "application/octet-stream");
        var clientId = Guid.CreateVersion7();

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/media",
            new { uploadId, clientId, fileName = "foto.png", caption = "mira" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Image", "Sent", "image/jpeg"), (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("media").GetProperty("mimeType").GetString()));
        var id = message.GetProperty("id").GetGuid();
        var send = Assert.Single(Sends(f.Factory.MetaHandler), request => !IsReadReceipt(request));
        using var body = JsonDocument.Parse(send.Body!);
        var attachment = body.RootElement.GetProperty("message").GetProperty("attachment");
        Assert.Equal("image", attachment.GetProperty("type").GetString());
        var key = MessagingMediaKeys.For(f.Tenant.TenantId, id);
        Assert.Equal($"https://r2.test/{key}?signed=1", attachment.GetProperty("payload").GetProperty("url").GetString());
        Assert.Single(f.Bucket.DownloadUrls, entry => entry.Key == key);
        // Sin subida a Meta: la única llamada a Graph es el envío.
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/media", StringComparison.Ordinal));
        Assert.True(f.Bucket.Objects.ContainsKey(key));
    }

    [Fact]
    public async Task AWordDocumentIsRejectedOnInstagram()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var uploadId = Guid.CreateVersion7();
        f.Bucket.Objects[MessagingMediaKeys.Staging(f.Tenant.TenantId, f.ConversationId, f.MemberId, uploadId, ".docx")] = (new byte[16], "application/octet-stream");

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/media",
            new { uploadId, clientId = Guid.CreateVersion7(), fileName = "propuesta.docx" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.media.unsupported_format", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.Empty(Sends(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AVoiceNoteIsTranscodedToWavStoredAndSentAsAnAudioAttachment()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler, "mid.voice");
        var clientId = Guid.CreateVersion7();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(clientId.ToString()), "clientId");
        var file = new ByteArrayContent(OggOpusFixture.VoiceNote());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "file", "voice.ogg");
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/audio") { Content = form };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");

        var response = await f.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Audio", "Sent", "audio/wav"), (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("media").GetProperty("mimeType").GetString()));
        var key = MessagingMediaKeys.For(f.Tenant.TenantId, message.GetProperty("id").GetGuid());
        var stored = f.Bucket.Objects[key];
        Assert.Equal("audio/wav", stored.ContentType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(stored.Bytes, 0, 4));
        var send = Assert.Single(Sends(f.Factory.MetaHandler), request => !IsReadReceipt(request));
        using var body = JsonDocument.Parse(send.Body!);
        var attachment = body.RootElement.GetProperty("message").GetProperty("attachment");
        Assert.Equal("audio", attachment.GetProperty("type").GetString());
        Assert.Equal($"https://r2.test/{key}?signed=1", attachment.GetProperty("payload").GetProperty("url").GetString());
        Assert.Equal("audio/wav|" + stored.Bytes.Length, await ScalarAsync<string>(f.ConnectionString,
            "SELECT media.mime_type || '|' || media.size_bytes FROM messaging.message_media media JOIN messaging.messages m ON m.id = media.message_id WHERE m.client_id = @c", ("c", clientId)));
    }

    [Fact]
    public async Task MarkingReadSendsMarkSeenToThePerson()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var seen = Assert.Single(Sends(f.Factory.MetaHandler));
        using var body = JsonDocument.Parse(seen.Body!);
        Assert.Equal("mark_seen", body.RootElement.GetProperty("sender_action").GetString());
        Assert.Equal(UserId, body.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT sum(unread_count) FROM messaging.conversations"));
    }

    [Fact]
    public async Task TemplatesAndNewConversationsAreChannelUnsupportedOnInstagram()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var template = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/template",
            new { clientId = Guid.CreateVersion7(), name = "seguimiento", language = "es", parameters = new Dictionary<string, string>() });
        var catalog = await f.Client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates", Ct);
        var customerId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations");
        var start = await SendAsync(f.Client, HttpMethod.Post, ConversationsUrl(f.Tenant.TenantId),
            new { clientId = Guid.CreateVersion7(), connectionId = f.ConnectionId, customerId, template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string>() } });

        foreach (var response in new[] { template, start })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("messaging.channel_unsupported", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        }

        // §5.2 (BFF): el catálogo de una conexión de Instagram es una lista vacía, no un error.
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal(0, (await catalog.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetArrayLength());
        Assert.Empty(Sends(f.Factory.MetaHandler));
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.Contains("message_templates", StringComparison.Ordinal));
    }

    /// <summary>Un PNG 2x2 válido armado a mano, copia de <c>SendMediaMessageApiTests</c>.</summary>
    private static byte[] Png()
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, 2);
        WriteBigEndian(header, 4, 2);
        header[8] = 8;
        header[9] = 2;
        using var raw = new MemoryStream();
        for (var row = 0; row < 2; row++)
        {
            raw.WriteByte(0);
            raw.Write([200, 30, 30, 30, 200, 30]);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw.ToArray());
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(typeAndData)));
        destination.Write(length);
        destination.Write(typeAndData);
        destination.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
