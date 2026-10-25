using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 D16, D16a, D16b y D16c: la plantilla de cotización sale al hilo del número en la conexión, sin las guardas
/// de la bandeja (ni estado, ni dueño, ni permisos de Mensajería), con el PDF como media propia de Mensajería. El servicio se resuelve
/// de un scope, como el del bot; el bucket es un doble en memoria.</summary>
public sealed class QuotationTemplateOutboundTests
{
    private const string Phone = "573001234567";
    private const string FileName = "Cotizacion-COT-1.pdf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>El R2 final de Mensajería en memoria: la plantilla sube el PDF por stream, nunca por staging.</summary>
    private sealed class MemoryBucket : IMessagingMediaStore
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

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, MemoryBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConnectionId, Guid MemberId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var bucket = new MemoryBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(bucket)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Fixture(factory, host, bucket, connectionString, tenant, connectionId, memberId);
    }

    private static void ScriptMetaOk(FakeMetaGraphHandler meta)
    {
        meta.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        meta.Respond("/111/messages$", HttpStatusCode.OK,
            """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.q"}]}""");
    }

    /// <summary>Las llamadas a Graph de la conexión, en orden, sin los acuses de leído.</summary>
    private static List<MetaRequest> MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal) && !IsReadReceipt(request)).ToList();

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    private static QuotationTemplateSend Send(Fixture f, string waId = Phone, Guid? customerId = null, string fullName = "Laura Gómez") =>
        new(f.Tenant.TenantId, f.ConnectionId, f.MemberId, waId, customerId, "Laura Gómez", "qep_cotizacion_v2", "es",
            fullName, "COT-1", "2.450.000 COP", "30 de septiembre de 2026", Pdf(), FileName);

    private static async Task<QuotationTemplateSendResult> SendAsync(Fixture f, QuotationTemplateSend send)
    {
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuotationTemplateOutbound>().SendAsync(send, Ct);
    }

    [Fact]
    public async Task SendsTheTemplateWithItsDocumentIntoANewOpenUnassignedConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var customerId = Guid.CreateVersion7();

        var sent = Assert.IsType<QuotationTemplateSendResult.Sent>(await SendAsync(f, Send(f, customerId: customerId)));

        Assert.Equal($"Open||{Phone}||{customerId}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT status || '|' || coalesce(assigned_member_id::text, '') || '|' || wa_id || '|' || coalesce(user_id, '') || '|' || customer_id
            FROM messaging.conversations WHERE id = @id
            """,
            ("id", sent.ConversationId)));
        var text = QuotationTemplateDefinition.Render(
            QuotationTemplateDefinition.ParametersFor("Laura Gómez", "COT-1", "2.450.000 COP", "30 de septiembre de 2026"));
        Assert.Equal($"{sent.ConversationId}|{f.MemberId}|qep_cotizacion_v2|1|{text}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT conversation_id || '|' || sent_by_member_id || '|' || (details->'template'->>'name') || '|' || status || '|' || text
            FROM messaging.messages WHERE id = @id
            """,
            ("id", sent.MessageId)));
        var key = $"messaging/{f.Tenant.TenantId}/{sent.MessageId}";
        Assert.Equal($"application/pdf|{FileName}|media.out|{key}|true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT mime_type || '|' || file_name || '|' || meta_media_id || '|' || storage_key || '|' || (stored_at IS NOT NULL) FROM messaging.message_media WHERE message_id = @id",
            ("id", sent.MessageId)));
        Assert.Equal(Pdf(), f.Bucket.Objects[key].Bytes);

        // Primero la subida del PDF a Meta y después la plantilla, por teléfono y con el documento como encabezado.
        var calls = MetaCalls(f);
        Assert.Equal(["/v24.0/111/media", "/v24.0/111/messages"], calls.Select(call => call.Uri!.AbsolutePath));
        using var body = JsonDocument.Parse(calls[1].Body!);
        Assert.Equal(Phone, body.RootElement.GetProperty("to").GetString());
        var template = body.RootElement.GetProperty("template");
        Assert.Equal("qep_cotizacion_v2", template.GetProperty("name").GetString());
        var header = template.GetProperty("components")[0];
        Assert.Equal("header", header.GetProperty("type").GetString());
        var document = header.GetProperty("parameters")[0].GetProperty("document");
        Assert.Equal(("media.out", FileName), (document.GetProperty("id").GetString(), document.GetProperty("filename").GetString()));
    }

    [Fact]
    public async Task UsesTheExistingConversationOfThatPhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        using (var anonymous = f.Host.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", Phone, "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(f.Host);
        }

        var existing = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<QuotationTemplateSendResult.Sent>(await SendAsync(f, Send(f, customerId: Guid.CreateVersion7())));

        Assert.Equal(existing, sent.ConversationId);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }

    /// <summary>Review Focus 4 (D16a): enviar la cotización no reabre, no toma ni marca leído, y no sube la versión.</summary>
    [Fact]
    public async Task AResolvedConversationAssignedToOtherKeepsItsStateAssignmentAndUnread()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var other = (await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Otra asesora", "advisor")).MembershipId;
        var conversationId = await SeedConversationAsync(f.Host, f.Tenant.TenantId, f.ConnectionId, Phone);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET status = 'Resolved', assigned_member_id = @member, assigned_at = now(), unread_count = 2 WHERE id = @id",
            ("member", other), ("id", conversationId));
        var before = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations WHERE id = @id", ("id", conversationId));
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<QuotationTemplateSendResult.Sent>(await SendAsync(f, Send(f)));

        Assert.Equal(conversationId, sent.ConversationId);
        Assert.Equal($"Resolved|{other}|2|{before}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || assigned_member_id || '|' || unread_count || '|' || version FROM messaging.conversations WHERE id = @id",
            ("id", conversationId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id AND direction = 2 AND status = 1", ("id", conversationId)));
    }

    /// <summary>Review Focus 3 (D16b): con destinatario Billing la conversación se busca sólo por teléfono y nace sin cliente.</summary>
    [Fact]
    public async Task BillingIsLookedUpByPhoneOnlyAndCarriesNoCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var customer = Guid.CreateVersion7();
        var customerConversation = await SeedCustomerConversationAsync(f, customer, "573001111111");
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<QuotationTemplateSendResult.Sent>(await SendAsync(f, Send(f, waId: "573002222222", customerId: null)));

        Assert.NotEqual(customerConversation, sent.ConversationId);
        Assert.Equal("573002222222|", await ScalarAsync<string>(f.ConnectionString,
            "SELECT wa_id || '|' || coalesce(customer_id::text, '') FROM messaging.conversations WHERE id = @id", ("id", sent.ConversationId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id", ("id", customerConversation)));
    }

    /// <summary>Review Focus 3 (D16b): el cliente viaja a la conversación nueva, pero no sirve para encontrar la suya en otro número.</summary>
    [Fact]
    public async Task ACustomerConversationOnAnotherNumberIsNotReused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var customer = Guid.CreateVersion7();
        var customerConversation = await SeedCustomerConversationAsync(f, customer, "573001111111");
        ScriptMetaOk(f.Factory.MetaHandler);

        var sent = Assert.IsType<QuotationTemplateSendResult.Sent>(await SendAsync(f, Send(f, waId: "573002222222", customerId: customer)));

        Assert.NotEqual(customerConversation, sent.ConversationId);
        Assert.Equal($"573002222222|{customer}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT wa_id || '|' || customer_id FROM messaging.conversations WHERE id = @id", ("id", sent.ConversationId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id", ("id", customerConversation)));
    }

    [Fact]
    public async Task AConnectionThatIsNotActiveReturnsConnectionUnavailableAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        ScriptMetaOk(f.Factory.MetaHandler);

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new QuotationTemplateSendResult.ConnectionUnavailable("paused"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
    }

    /// <summary>Review Focus 5 (D16c): Meta rechaza la plantilla; el mensaje queda Failed y su documento sigue en el bucket, para que
    /// el hilo lo muestre.</summary>
    [Fact]
    public async Task MetaRejectingTheTemplateLeavesAFailedMessageWithItsDocument()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        f.Factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015));

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new QuotationTemplateSendResult.Failed("messaging.message.rejected"), result);
        var messageId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 2");
        var key = $"messaging/{f.Tenant.TenantId}/{messageId}";
        Assert.Equal($"4|132015|media.out|{key}", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT m.status || '|' || m.failure_code || '|' || mm.meta_media_id || '|' || mm.storage_key
            FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id WHERE m.id = @id
            """,
            ("id", messageId)));
        Assert.Equal(Pdf(), f.Bucket.Objects[key].Bytes);
    }

    [Fact]
    public async Task AFailedUploadToMetaSendsNothingAndLeavesNoRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        f.Factory.MetaHandler.Respond("/111/media$", HttpStatusCode.InternalServerError, "{}");

        var result = await SendAsync(f, Send(f));

        Assert.Equal(new QuotationTemplateSendResult.Failed("messaging.media.upload_failed"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.DoesNotContain(MetaCalls(f), call => call.Uri!.AbsolutePath == "/v24.0/111/messages");
        Assert.Empty(f.Bucket.Objects);
    }

    [Fact]
    public async Task AnInvalidValueIsRejectedBeforeAnyCall()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);

        var result = await SendAsync(f, Send(f, fullName: "Juan\nPerez"));

        Assert.Equal(new QuotationTemplateSendResult.Failed("messaging.message.rejected"), result);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Empty(MetaCalls(f));
    }

    /// <summary>La conversación del cliente en su propio número, con su <c>customer_id</c>, como la dejaría la bandeja.</summary>
    private static async Task<Guid> SeedCustomerConversationAsync(Fixture f, Guid customerId, string waId)
    {
        var id = await SeedConversationAsync(f.Host, f.Tenant.TenantId, f.ConnectionId, waId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @customer WHERE id = @id", ("customer", customerId), ("id", id));
        return id;
    }
}
