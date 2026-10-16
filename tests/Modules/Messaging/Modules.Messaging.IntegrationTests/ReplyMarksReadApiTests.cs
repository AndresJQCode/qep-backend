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

/// <summary>Spec 2026-10-16 §3.1 por HTTP y contra la base: todo saliente que Meta acepta (texto, plantilla, nota de voz, adjunto,
/// <c>POST /conversations</c> sobre una conversación existente e Instagram) resta lo que el asesor tenía a la vista, sin subir
/// <c>version</c> ni <c>updated_at</c>, y manda el acuse sólo si el contador quedó en 0. Un rechazo no toca nada, y un entrante
/// que llega durante el envío sobrevive y frena el acuse.</summary>
public sealed class ReplyMarksReadApiTests
{
    private const string SendOk = """{"messaging_product":"whatsapp","messages":[{"id":"wamid.out"}]}""";

    private const string Catalog = """
        {"data":[{"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
                  "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Staging y R2 final en un diccionario, como en <c>SendMediaMessageApiTests</c>: sirve al adjunto y a la nota de voz.</summary>
    private sealed class FakeBucket : IMessagingMediaStore, IMessagingUploadStaging
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

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

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConversationId, Guid MemberId, HttpClient Client);

    /// <summary>El arreglo de <c>SendMessageApiTests</c> (el entrante <c>w1</c> deja <c>unread_count = 1</c>) con el bucket de
    /// <c>SendMediaMessageApiTests</c> y el catálogo de <c>SendTemplateApiTests</c>. Después se fuerza <c>unread_count = 2</c> y la
    /// conversación queda del owner: la autoasignación sí sube <c>version</c>, y es otra cosa.</summary>
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
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET unread_count = 2, assigned_member_id = @m, assigned_at = now()", ("m", memberId));
        return new Fixture(factory, host, bucket, connectionString, tenant, conversationId, memberId, client);
    }

    private static Task<int> UnreadAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, "SELECT unread_count FROM messaging.conversations");

    private static Task<HttpResponseMessage> SendTextAsync(Fixture f, Guid? clientId = null) =>
        SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = clientId ?? Guid.CreateVersion7(), text = "Sí, tenemos." });

    private static MetaRequest[] ReceiptsSent(FakeMetaGraphHandler meta) =>
        meta.Requests.Where(request => request.Body?.Contains("\"status\":\"read\"", StringComparison.Ordinal) == true).ToArray();

    [Fact]
    public async Task ATextReplyClearsTheMarkAndAcknowledgesTheLastInbound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, SendOk);
        var before = await ScalarAsync<string>(f.ConnectionString, "SELECT version || '|' || updated_at::text FROM messaging.conversations");

        var response = await SendTextAsync(f);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
        // §3.1 punto 2: responder no sube version ni updated_at, igual que el envío.
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString, "SELECT version || '|' || updated_at::text FROM messaging.conversations"));
        var receipt = Assert.Single(ReceiptsSent(f.Factory.MetaHandler));
        Assert.EndsWith("/111/messages", receipt.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":\"w1\"", receipt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateReplyClearsTheMark()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, SendOk);

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{MessagesUrl(f.Tenant.TenantId, f.ConversationId)}/template", new
        {
            clientId = Guid.CreateVersion7(), name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
        Assert.Single(ReceiptsSent(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AVoiceNoteReplyClearsTheMark()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, SendOk);
        var file = new ByteArrayContent(OggOpusFixture.VoiceNote());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        using var form = new MultipartFormDataContent { { new StringContent(Guid.CreateVersion7().ToString()), "clientId" }, { file, "file", "voice.ogg" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/audio") { Content = form };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");

        var response = await f.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
        Assert.Single(ReceiptsSent(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AnAttachmentReplyClearsTheMark()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, SendOk);
        var uploadId = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Staging(f.Tenant.TenantId, f.ConversationId, f.MemberId, uploadId, ".pdf");
        f.Bucket.Objects[key] = (Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"), "application/octet-stream");

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/media",
            new { uploadId, clientId = Guid.CreateVersion7(), fileName = "contrato.pdf" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
        Assert.Single(ReceiptsSent(f.Factory.MetaHandler));
    }

    /// <summary>El arreglo de <c>StartConversationApiTests</c>: la primera plantilla crea la conversación (nada sin leer, no-op), la
    /// persona contesta y la segunda <c>POST /conversations</c> cae en la misma conversación (200): también es responder.</summary>
    [Fact]
    public async Task StartingAnExistingConversationWithATemplateClearsTheMark()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        // Un wamid por envío, como en StartConversationApiTests: dos plantillas en la misma conversación no pueden repetirlo.
        var sent = 0;
        factory.MetaHandler.RespondWith("/111/messages", _ => FakeMetaGraphHandler.Json(
            HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.start.{{Interlocked.Increment(ref sent)}}"}]}"""));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        object Body() => new
        {
            clientId = Guid.CreateVersion7(), connectionId, customerId,
            template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } },
        };
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, ConversationsUrl(tenant.TenantId), Body())).StatusCode);
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "sí, cuéntame"));
        await DrainDeliveriesAsync(factory);
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET unread_count = 2");

        var response = await SendAsync(client, HttpMethod.Post, ConversationsUrl(tenant.TenantId), Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(connectionString));
        Assert.Single(ReceiptsSent(factory.MetaHandler));
    }

    /// <summary>El arreglo de <c>InstagramSendApiTests</c>: en Instagram el acuse es <c>mark_seen</c>, por persona.</summary>
    [Fact]
    public async Task AnInstagramReplyClearsTheMarkWithMarkSeen()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", HttpStatusCode.OK, """{"name":"Laura Pérez","username":"laura.perez","id":"1234567890123456"}""");
        using var anonymous = factory.CreateClient();
        await PostInstagramWebhookAsync(anonymous, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET unread_count = 2, assigned_member_id = @m, assigned_at = now()",
            ("m", await OwnerMembershipIdAsync(connectionString, tenant)));
        var sendPath = $"/v24.0/{InstagramPayloads.DefaultAccountId}/messages";
        factory.MetaHandler.Respond($"^{sendPath}$", HttpStatusCode.OK, $$"""{"recipient_id":"{{InstagramPayloads.DefaultUserId}}","message_id":"mid.out"}""");

        var response = await SendAsync(client, HttpMethod.Post, MessagesUrl(tenant.TenantId, conversationId), new { clientId = Guid.CreateVersion7(), text = "¡Claro que sí!" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(connectionString));
        Assert.Single(factory.MetaHandler.Requests, request =>
            request.Uri!.AbsolutePath == sendPath && request.Body?.Contains("\"sender_action\":\"mark_seen\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ARejectedSendLeavesTheMarkAndSendsNoReceipt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var response = await SendTextAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(2, await UnreadAsync(f.ConnectionString));
        Assert.Empty(ReceiptsSent(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AnInboundDuringTheSendSurvivesAndNoReceiptGoesOut()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.RespondWith("/111/messages", _ =>
        {
            ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = unread_count + 1").GetAwaiter().GetResult();
            return FakeMetaGraphHandler.Json(HttpStatusCode.OK, SendOk);
        });

        var response = await SendTextAsync(f);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await UnreadAsync(f.ConnectionString));
        Assert.Empty(ReceiptsSent(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task ACounterBelowWhatTheAdvisorSawIsLeftAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.RespondWith("/111/messages", _ =>
        {
            ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 1").GetAwaiter().GetResult();
            return FakeMetaGraphHandler.Json(HttpStatusCode.OK, SendOk);
        });

        var response = await SendTextAsync(f);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await UnreadAsync(f.ConnectionString));
    }

    [Fact]
    public async Task ARetriedFailedSendClearsWhatTheRetrySaw()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var clientId = Guid.CreateVersion7();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.InternalServerError, FakeMetaGraphHandler.GraphError(131016));

        var failed = await SendTextAsync(f, clientId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(2, await UnreadAsync(f.ConnectionString));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 3");
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, SendOk);

        var retry = await SendTextAsync(f, clientId);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
    }

    [Fact]
    public async Task AReplyWithNothingUnreadSendsNoReceipt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 0");
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, SendOk);

        var response = await SendTextAsync(f);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(f.ConnectionString));
        Assert.Empty(ReceiptsSent(f.Factory.MetaHandler));
    }
}
