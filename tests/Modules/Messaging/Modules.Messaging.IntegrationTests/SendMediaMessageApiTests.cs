using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Messaging-media-reply R3 a R7, R10, R13 a R15 y R17 por HTTP: la confirmación del adjunto responde 201 con la burbuja
/// (<c>Image</c> o <c>Document</c>), repite por <c>clientId</c> sin volver a subir, devuelve 422 con el código de dominio o el mapa
/// <c>errors</c>, y 403 (nunca 404) desde otro tenant. El bucket es un doble que sirve de staging y de R2 final a la vez; Graph es
/// el <see cref="FakeMetaGraphHandler"/> del harness, que también contesta el multipart del cliente <c>messaging.meta-graph-media</c>.</summary>
public sealed class SendMediaMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un solo diccionario para el staging y el R2 final: el documento se promueve copiando, la imagen se sube por stream.</summary>
    private sealed class FakeBucket : IMessagingMediaStore, IMessagingUploadStaging
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        /// <summary>Un tamaño distinto al real, para probar el tope sin escribir 100 MB.</summary>
        public Dictionary<string, long> SizeOverrides { get; } = new(StringComparer.Ordinal);

        public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new MessagingUploadUrl(new Uri($"https://r2.test/{key}"), DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<StagedUpload?>(Objects.TryGetValue(key, out var stored)
                ? new StagedUpload(SizeOverrides.TryGetValue(key, out var size) ? size : stored.Bytes.Length, "etag-" + stored.Bytes.Length)
                : null);

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

        /// <summary>Spec 2026-10-14 decisión 12: la URL que Meta bajaría; en pruebas apunta al handler falso de Graph.</summary>
        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConversationId, Guid MemberId, HttpClient Client);

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
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Fixture(factory, host, bucket, connectionString, tenant, conversationId, memberId, client);
    }

    private static string MediaSendUrl(Fixture f) => MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/media";

    private static void ScriptMetaOk(FakeMetaGraphHandler meta)
    {
        meta.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        meta.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.media"}]}""");
    }

    private static int MetaCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal) && !IsReadReceipt(request));

    /// <summary>Deja el archivo en el staging con la clave que reconstruye el handler y devuelve su <c>uploadId</c>.</summary>
    private static Guid Stage(Fixture f, string fileName, byte[] bytes)
    {
        var uploadId = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Staging(f.Tenant.TenantId, f.ConversationId, f.MemberId, uploadId, Path.GetExtension(fileName));
        f.Bucket.Objects[key] = (bytes, "application/octet-stream");
        return uploadId;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Fixture f, Guid? uploadId, string fileName, Guid? clientId = null, string? caption = null, Guid? replyTo = null) =>
        SendAsync(f.Client, HttpMethod.Post, MediaSendUrl(f), new { uploadId, clientId = clientId ?? Guid.CreateVersion7(), fileName, caption, replyTo });

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    /// <summary>Un PNG 2x2 válido armado a mano (el proyecto de pruebas no referencia ImageSharp): firma, IHDR, IDAT y IEND.</summary>
    private static byte[] Png()
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, 2);
        WriteBigEndian(header, 4, 2);
        header[8] = 8; // profundidad
        header[9] = 2; // RGB
        using var raw = new MemoryStream();
        for (var row = 0; row < 2; row++)
        {
            raw.WriteByte(0); // sin filtro
            raw.Write([200, 30, 30, 30, 200, 30]);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
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
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
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
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    [Fact]
    public async Task ADocumentAnswers201AsDocumentWithItsNameAndCaptionAndConsumesTheUpload()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var pdf = Pdf();
        var uploadId = Stage(f, "contrato.pdf", pdf);
        var clientId = Guid.CreateVersion7();

        var response = await ConfirmAsync(f, uploadId, "contrato.pdf", clientId, caption: "Te adjunto el contrato");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = message.GetProperty("id").GetGuid();
        Assert.Equal(("Document", "Sent", clientId), (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("clientId").GetGuid()));
        var media = message.GetProperty("media");
        Assert.Equal(("application/pdf", "contrato.pdf", "Te adjunto el contrato"),
            (media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").GetString(), media.GetProperty("caption").GetString()));

        // Meta: el multipart lleva un nombre genérico y el real viaja en document.filename.
        var upload = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", upload.Authorization);
        Assert.Contains("application/pdf", upload.Body, StringComparison.Ordinal);
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));
        Assert.Contains("\"filename\":\"contrato.pdf\"", send.Body, StringComparison.Ordinal);
        Assert.Contains("\"caption\":\"Te adjunto el contrato\"", send.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("context", send.Body, StringComparison.Ordinal);

        // R2: el documento se promovió a la clave final y el staging se consumió.
        Assert.Equal(pdf, f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{id}"].Bytes);
        Assert.DoesNotContain(f.Bucket.Objects.Keys, key => key.StartsWith("messaging/staging/", StringComparison.Ordinal));
        Assert.Equal($"media.out|{pdf.Length}|contrato.pdf|Te adjunto el contrato", await ScalarAsync<string>(f.ConnectionString,
            "SELECT mm.meta_media_id || '|' || mm.size_bytes || '|' || mm.file_name || '|' || m.caption FROM messaging.message_media mm JOIN messaging.messages m ON m.id = mm.message_id WHERE mm.message_id = @id",
            ("id", id)));
    }

    [Fact]
    public async Task AnImageIsRecompressedToJpegAndAnswers201AsImageWithTheReplyContext()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var uploadId = Stage(f, "foto.png", Png());
        var quoted = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'w1'");

        var response = await ConfirmAsync(f, uploadId, "foto.png", replyTo: quoted);

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = message.GetProperty("id").GetGuid();
        Assert.Equal("Image", message.GetProperty("kind").GetString());
        var media = message.GetProperty("media");
        Assert.Equal(("image/jpeg", "foto.jpg", JsonValueKind.Null), (media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").GetString(), media.GetProperty("caption").ValueKind));
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));
        Assert.Contains("\"image\":{\"id\":\"media.out\"", send.Body, StringComparison.Ordinal);
        Assert.Contains("\"context\":{\"message_id\":\"w1\"}", send.Body, StringComparison.Ordinal);
        Assert.Equal(quoted, message.GetProperty("replyTo").GetProperty("id").GetGuid());

        // R2 guarda la comprimida (JPEG, firma FFD8FF), no el PNG original.
        var stored = f.Bucket.Objects[$"messaging/{f.Tenant.TenantId}/{id}"];
        Assert.Equal("image/jpeg", stored.ContentType);
        Assert.Equal([0xFF, 0xD8, 0xFF], stored.Bytes[..3]);
    }

    private static byte[] Mp4() => [0, 0, 0, 0x18, .. "ftypmp42"u8, 0, 0, 0, 0, .. "isommp42"u8, 0, 0, 0, 8, .. "free"u8, .. new byte[64]];

    private static byte[] Mp3() => [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x64, .. new byte[64]];

    // Spec 2026-10-20 D3 y D5: el video sale como type video con su texto y queda Video con su nombre.
    [Fact]
    public async Task AVideoAnswers201AsVideoWithItsNameAndCaption()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var uploadId = Stage(f, "producto.mp4", Mp4());

        var response = await ConfirmAsync(f, uploadId, "producto.mp4", caption: "Mira el producto");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = message.GetProperty("id").GetGuid();
        Assert.Equal("Video", message.GetProperty("kind").GetString());
        var media = message.GetProperty("media");
        Assert.Equal(("video/mp4", "producto.mp4", "Mira el producto"),
            (media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").GetString(), media.GetProperty("caption").GetString()));
        var upload = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        Assert.Contains("video/mp4", upload.Body, StringComparison.Ordinal);
        Assert.Contains("video.mp4", upload.Body, StringComparison.Ordinal);
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));
        Assert.Contains("\"video\":{\"id\":\"media.out\",\"caption\":\"Mira el producto\"}", send.Body, StringComparison.Ordinal);
        Assert.Equal("3|producto.mp4|video/mp4", await ScalarAsync<string>(f.ConnectionString,
            "SELECT m.kind || '|' || mm.file_name || '|' || mm.mime_type FROM messaging.message_media mm JOIN messaging.messages m ON m.id = mm.message_id WHERE mm.message_id = @id",
            ("id", id)));
    }

    // D5 y D7: un audio adjunto sale sin voice y guarda su nombre (la nota de voz guarda null); con texto es 422 en caption.
    [Fact]
    public async Task AFileAudioAnswers201AsAudioWithItsNameWithoutVoiceAndACaptionIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var withCaption = await ConfirmAsync(f, Stage(f, "cancion.mp3", Mp3()), "cancion.mp3", caption: "Escúchalo");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withCaption.StatusCode);
        var problem = await ProblemAsync(withCaption);
        Assert.Equal("validation.failed", problem.Code);
        Assert.Equal(["caption"], problem.ErrorKeys);
        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));

        var response = await ConfirmAsync(f, Stage(f, "cancion.mp3", Mp3()), "cancion.mp3");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Audio", message.GetProperty("kind").GetString());
        var media = message.GetProperty("media");
        Assert.Equal(("audio/mpeg", "cancion.mp3", JsonValueKind.Null),
            (media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").GetString(), media.GetProperty("caption").ValueKind));
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));
        Assert.Contains("\"audio\":{\"id\":\"media.out\"}", send.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("voice", send.Body, StringComparison.Ordinal);
        Assert.Equal("4|cancion.mp3", await ScalarAsync<string>(f.ConnectionString,
            "SELECT m.kind || '|' || mm.file_name FROM messaging.message_media mm JOIN messaging.messages m ON m.id = mm.message_id WHERE mm.message_id = @id",
            ("id", message.GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredMessageWithoutSendingAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var uploadId = Stage(f, "contrato.pdf", Pdf());
        var clientId = Guid.CreateVersion7();

        var first = await (await ConfirmAsync(f, uploadId, "contrato.pdf", clientId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        // El staging ya se consumió: gana la idempotencia, no el upload_not_found.
        var again = await ConfirmAsync(f, uploadId, "contrato.pdf", clientId);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Document", second.GetProperty("kind").GetString());
        Assert.Equal(2, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ABadBodyIs422WithTheFieldMapAndAnInvalidReplyToIs422OnReplyTo()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var uploadId = Stage(f, "contrato.pdf", Pdf());

        var shape = await SendAsync(f.Client, HttpMethod.Post, MediaSendUrl(f), new { fileName = "" });
        var badReply = await ConfirmAsync(f, uploadId, "contrato.pdf", replyTo: Guid.CreateVersion7());
        var emptyReply = await ConfirmAsync(f, uploadId, "contrato.pdf", replyTo: Guid.Empty);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shape.StatusCode);
        var problem = await ProblemAsync(shape);
        Assert.Equal("validation.failed", problem.Code);
        Assert.Equal(["clientId", "fileName", "uploadId"], problem.ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badReply.StatusCode);
        Assert.Equal(["replyTo"], (await ProblemAsync(badReply)).ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, emptyReply.StatusCode);
        Assert.Equal(["replyTo"], (await ProblemAsync(emptyReply)).ErrorKeys);
        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    // Task 2.5: cada falla de contenido es un DomainException que ApiExceptionHandler vuelve 422 con su código; nada se manda a Meta.
    [Fact]
    public async Task EveryContentFailureIs422WithItsDomainCodeAndNothingReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);

        var notFound = await ConfirmAsync(f, Guid.CreateVersion7(), "contrato.pdf");

        var bigId = Stage(f, "gigante.pdf", Pdf());
        f.Bucket.SizeOverrides[MessagingMediaKeys.Staging(f.Tenant.TenantId, f.ConversationId, f.MemberId, bigId, ".pdf")] = OutboundMediaPolicy.MaxDocumentBytes + 1;
        var tooLarge = await ConfirmAsync(f, bigId, "gigante.pdf");

        var exeId = Stage(f, "falso.pdf", Encoding.ASCII.GetBytes("MZ-esto-no-es-un-pdf-0123456789"));
        var unsupportedFormat = await ConfirmAsync(f, exeId, "falso.pdf");

        var extensionId = Stage(f, "nota.txt", Pdf());
        var unsupportedExtension = await ConfirmAsync(f, extensionId, "nota.txt");

        var brokenPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3, 4 };
        var imageId = Stage(f, "rota.png", brokenPng);
        var invalidImage = await ConfirmAsync(f, imageId, "rota.png");

        foreach (var (response, code) in new[]
        {
            (notFound, "messaging.media.upload_not_found"),
            (tooLarge, "messaging.media.too_large"),
            (unsupportedFormat, "messaging.media.unsupported_format"),
            (unsupportedExtension, "messaging.media.unsupported_format"),
            (invalidImage, "messaging.media.invalid_image"),
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(code, (await ProblemAsync(response)).Code);
        }

        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ACallFromAnotherTenantOrWithoutManageIsForbiddenAndLeavesTheUploadUntouched()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var uploadId = Stage(f, "contrato.pdf", Pdf());
        var other = await RegisterTenantAsync(f.Host);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Host, other.OwnerUserId, other.TenantId, ManagePermissions);
        using var reader = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var body = new { uploadId, clientId = Guid.CreateVersion7(), fileName = "contrato.pdf" };

        var cross = await SendAsync(otherClient, HttpMethod.Post, MediaSendUrl(f), body);
        var readOnly = await SendAsync(reader, HttpMethod.Post, MediaSendUrl(f), body);

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        // El staging sigue intacto: ninguna de las dos lo tocó.
        Assert.Single(f.Bucket.Objects);
    }
}
