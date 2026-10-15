using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §4.1, §6 y §10: multipart real con una nota OGG/Opus; <c>kind = 4</c>, la fila de
/// <c>message_media</c> con <c>stored_at</c> y el GET de media con los mismos bytes; validación por campo; 413 arriba
/// del límite (con y sin <c>Content-Length</c>); R2 caído es 422 sin fila. El CSRF con auth real vive en
/// <c>Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests</c> (este harness corre con el stub).</summary>
public sealed class SendAudioMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>R2 en un diccionario, como en <c>MediaApiTests</c>; <see cref="FailUploads"/> simula R2 caído.</summary>
    private sealed class FakeMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public bool FailUploads { get; set; }

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            if (FailUploads)
            {
                throw new IOException("R2 no responde");
            }

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

        /// <summary>Spec 2026-10-14 decisión 12: la URL que Meta bajaría; en pruebas apunta al handler falso de Graph.</summary>
        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    /// <summary>Sin <c>Content-Length</c>: <c>StreamContent</c> sobre un stream que no se puede recorrer viaja chunked.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMediaStore Store, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(store)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, host, store, connectionString, tenant, conversationId, client);
    }

    private static string AudioUrl(Fixture f) => MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/audio";

    private static void ScriptMetaOk(FakeMetaGraphHandler meta)
    {
        meta.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        meta.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.voice"}]}""");
    }

    private static int MetaCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal));

    private static MultipartFormDataContent VoiceForm(Guid? clientId, HttpContent file, string? replyTo = null)
    {
        var form = new MultipartFormDataContent();
        if (clientId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "clientId");
        }

        if (replyTo is not null)
        {
            form.Add(new StringContent(replyTo), "replyTo");
        }

        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "file", "voice.ogg");
        return form;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AVoiceNoteAnswers201AsAudioStoresItPrivatelyAndServesTheSameBytes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var audio = OggOpusFixture.VoiceNote();
        var clientId = Guid.CreateVersion7();

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(audio)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = message.GetProperty("id").GetGuid();
        Assert.Equal(("Audio", "Sent", JsonValueKind.Null, clientId),
            (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("text").ValueKind, message.GetProperty("clientId").GetGuid()));
        var media = message.GetProperty("media");
        Assert.Equal((MediaUrl(f.Tenant.TenantId, id), "audio/ogg", JsonValueKind.Null, JsonValueKind.Null),
            (media.GetProperty("url").GetString(), media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").ValueKind, media.GetProperty("caption").ValueKind));

        // Meta: subida multipart y envío como nota de voz, con el token de la conexión.
        var upload = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", upload.Authorization);
        Assert.Contains("voice.ogg", upload.Body, StringComparison.Ordinal);
        Assert.Contains("audio/ogg", upload.Body, StringComparison.Ordinal);
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages");
        Assert.Contains("\"audio\":{\"id\":\"media.out\",\"voice\":true}", send.Body, StringComparison.Ordinal);
        Assert.Contains($"\"biz_opaque_callback_data\":\"qep:{id}\"", send.Body, StringComparison.Ordinal);

        // Base: kind 4, medio guardado y fuera de la cola de copia, y la foto de la conversación como un audio entrante.
        Assert.Equal("4|true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || (text IS NULL) FROM messaging.messages WHERE id = @id", ("id", id)));
        Assert.Equal($"media.out|messaging/{f.Tenant.TenantId}/{id}|{audio.Length}|{Convert.ToHexStringLower(SHA256.HashData(audio))}|true",
            await ScalarAsync<string>(f.ConnectionString,
                "SELECT meta_media_id || '|' || storage_key || '|' || size_bytes || '|' || sha256 || '|' || (stored_at IS NOT NULL) FROM messaging.message_media WHERE message_id = @id",
                ("id", id)));
        Assert.Equal("4|true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_kind || '|' || (last_message_preview IS NULL) FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));

        // El GET de media de siempre devuelve los mismos bytes, inline.
        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, id), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("audio/ogg", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(audio, await served.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredVoiceNoteWithoutUploadingAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var first = await (await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(OggOpusFixture.VoiceNote())))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var again = await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(OggOpusFixture.VoiceNote())));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(("Audio", "audio/ogg"), (second.GetProperty("kind").GetString(), second.GetProperty("media").GetProperty("mimeType").GetString()));
        Assert.Equal(2, MetaCalls(f.Factory.MetaHandler));
    }

    // Review Focus 4 y 5: cada campo con su clave; un request que no es multipart es 422, no 415 ni 500.
    [Fact]
    public async Task TheFormIsValidatedFieldByField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);

        var webm = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.WebmHeader())));
        var noClient = await PostAsync(f.Client, AudioUrl(f), VoiceForm(null, new ByteArrayContent(OggOpusFixture.VoiceNote())));
        var badReply = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.VoiceNote()), replyTo: "no-es-un-guid"));
        var json = await PostAsync(f.Client, AudioUrl(f), JsonContent.Create(new { clientId = Guid.CreateVersion7(), text = "hola" }));

        // ProblemAsync ordena las claves en ordinal: «clientId» antes que «file».
        foreach (var (response, keys) in new[] { (webm, new[] { "file" }), (noClient, new[] { "clientId" }), (badReply, new[] { "replyTo" }), (json, new[] { "clientId", "file" }) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var (code, errorKeys) = await ProblemAsync(response);
            Assert.Equal("validation.failed", code);
            Assert.Equal(keys, errorKeys);
        }

        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Empty(f.Store.Objects);
    }

    [Fact]
    public async Task AnR2FailureIs422UploadFailedWithoutRowNorMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        f.Store.FailUploads = true;

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.VoiceNote())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.media.upload_failed", (await ProblemAsync(response)).Code);
        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ABodyOverTheLimitIs413()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var tooBig = new byte[(17 * 1024 * 1024) + 1];
        OggOpusFixture.VoiceNote().CopyTo(tooBig, 0);

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(tooBig)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    // Review Focus 3: sin Content-Length el tope lo pone MultipartBodyLengthLimit; tiene que ser 413, no 500.
    [Fact]
    public async Task AChunkedBodyOverTheLimitIs413()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var tooBig = new byte[(17 * 1024 * 1024) + 1];
        OggOpusFixture.VoiceNote().CopyTo(tooBig, 0);
        var form = VoiceForm(Guid.CreateVersion7(), new StreamContent(new UnseekableStream(tooBig)));
        Assert.Null(form.Headers.ContentLength);

        var response = await PostAsync(f.Client, AudioUrl(f), form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
