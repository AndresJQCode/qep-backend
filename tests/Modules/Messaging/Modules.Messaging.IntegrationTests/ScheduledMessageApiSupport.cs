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

/// <summary>Lo que comparten las pruebas HTTP y de auditoría de los programados (spec 2026-11-09): un host con una conversación abierta
/// (el cliente escribió hace un minuto, así que la ventana de 24 h cabe cualquier hora de pruebas), el bucket en memoria y Graph
/// scripteado para que el ejecutor pueda enviar.</summary>
internal sealed class ScheduledFixture : IDisposable
{
    public required QepApiFactory Factory { get; init; }

    public required WebApplicationFactory<Program> Host { get; init; }

    public required FakeMessagingBucket Bucket { get; init; }

    public required string ConnectionString { get; init; }

    public required RegisteredTenant Tenant { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid MemberId { get; init; }

    public required HttpClient Client { get; init; }

    public string ConversationUrl => MessagingApiHarness.ConversationUrl(Tenant.TenantId, ConversationId);

    public string ScheduledUrl => ConversationUrl + "/scheduled-message";

    public string SchedulesUrl => ConversationUrl + "/scheduled-messages";

    public string ScheduleUrl(Guid id) => $"{SchedulesUrl}/{id}";

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
        Factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ScheduledFixture> ArrangeAsync(TestDatabase database, Action<IServiceCollection>? configure = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
            configure?.Invoke(services);
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
        factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.scheduled"}]}""");
        return new ScheduledFixture
        {
            Factory = factory, Host = host, Bucket = bucket, ConnectionString = connectionString, Tenant = tenant, ConversationId = conversationId,
            MemberId = memberId, Client = client,
        };
    }

    public static DateTimeOffset InTwoHours() => DateTimeOffset.UtcNow.AddHours(2);

    public int MetaSends() =>
        Factory.MetaHandler.Requests.Count(request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));

    public Task<HttpResponseMessage> PostTextAsync(Guid clientId, DateTimeOffset scheduledAt, string text = "Te escribo como quedamos") =>
        SendAsync(Client, HttpMethod.Post, SchedulesUrl, new { clientId, scheduledAt, text });

    /// <summary>Crea un programado de texto y devuelve su cuerpo.</summary>
    public async Task<JsonElement> CreateTextAsync(Guid? clientId = null, DateTimeOffset? scheduledAt = null)
    {
        var response = await PostTextAsync(clientId ?? Guid.CreateVersion7(), scheduledAt ?? InTwoHours());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    /// <summary>Un documento por el mismo camino del asesor: el archivo ya subido al staging y su <c>uploadId</c> en el POST.</summary>
    public async Task<JsonElement> CreateDocumentAsync(Guid? clientId = null, DateTimeOffset? scheduledAt = null, string? caption = "El contrato")
    {
        var uploadId = Guid.CreateVersion7();
        Bucket.Objects[MessagingMediaKeys.Staging(Tenant.TenantId, ConversationId, MemberId, uploadId, ".pdf")] = (Pdf(), "application/pdf");
        var response = await SendAsync(Client, HttpMethod.Post, SchedulesUrl, new
        {
            clientId = clientId ?? Guid.CreateVersion7(), scheduledAt = scheduledAt ?? InTwoHours(), uploadId, fileName = "contrato.pdf", caption,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public Task<HttpResponseMessage> PostVoiceNoteAsync(Guid clientId, DateTimeOffset scheduledAt)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(clientId.ToString()), "clientId" },
            { new StringContent(scheduledAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), "scheduledAt" },
        };
        var file = new ByteArrayContent(OggOpusFixture.VoiceNote());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "file", "voice.ogg");
        return PostMultipartAsync(SchedulesUrl + "/audio", form);
    }

    public async Task<HttpResponseMessage> PostMultipartAsync(string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        return await Client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> ActAsync(Guid id, string action, long expectedVersion) =>
        SendAsync(Client, HttpMethod.Post, ScheduleUrl(id) + "/" + action, new { expectedVersion });

    public Task<HttpResponseMessage> EditAsync(Guid id, object body) => SendAsync(Client, HttpMethod.Put, ScheduleUrl(id), body);

    /// <summary>Lo que el worker deja cuando falla: <c>Failed</c> con su código, sin tocar la versión (la lee el GET).</summary>
    public Task<int> MakeFailedAsync(Guid id, string code = "messaging.window_closed") =>
        ExecuteAsync(ConnectionString,
            "UPDATE messaging.scheduled_messages SET status = 'Failed', failure_code = @code, locked_until = NULL WHERE id = @id", ("code", code), ("id", id));

    public async Task<long> VersionAsync() =>
        (await Client.GetFromJsonAsync<JsonElement>(ScheduledUrl, Ct)).GetProperty("scheduledMessage").GetProperty("version").GetInt64();

    public Task<long> AuditCountAsync(string action, Guid id) =>
        CountAsync(ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_type = 'scheduled_message' AND resource_id = @id",
            ("action", action), ("id", id.ToString()));
}
