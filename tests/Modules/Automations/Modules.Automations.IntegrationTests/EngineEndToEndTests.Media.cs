using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Messaging.Application;
using Modules.Storage.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 «Execution», de punta a punta: un chatbot publicado con un Mensaje con imagen la manda a Meta por link (la URL pública
/// del archivo) con su pie. Storage es el doble <see cref="FakeChatbotMediaStore"/>, sembrado antes de publicar porque el validador exige que
/// el archivo resuelva.
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private const string ImageFlow = """
        { "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [],
          "nodes": [
            { "id": "m1", "kind": "message", "title": "Catálogo", "text": "Catálogo de {nombre_contacto}", "urlButton": null,
              "media": { "kind": "image", "fileId": "{fileId}" }, "outputs": { "next": "h1" } },
            { "id": "h1", "kind": "handoff", "title": "Asesor", "text": null }
          ] }
        """;

    [Fact]
    public async Task APublishedImageMessageReachesMetaByLink()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var notifier = new RecordingNotifier();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessagingNotifier>();
            services.AddSingleton<IMessagingNotifier>(notifier);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio", PhoneNumberId, "222");
        var session = await factory.Media.OpenUploadAsync(tenant.TenantId, "catalogo.jpg", "image/jpeg", 2048, 25_000_000, Ct);
        await factory.Media.CompleteAsync(tenant.TenantId, session.FileId, Ct);
        using var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var chatbotId = await PublishChatbotAsync(
            admin, tenant.TenantId, ImageFlow.Replace("{fileId}", session.FileId.ToString(), StringComparison.Ordinal), connectionId);
        using var f = new Fixture(factory, host, connectionString, tenant, connectionId, chatbotId, host.CreateClient(), notifier, new BotMeta(factory.MetaHandler));

        await InboundAsync(f, "w1", "hola");
        await DrainAutomationsAsync(f.Host);

        var post = Assert.Single(
            f.Factory.MetaHandler.Requests,
            request => request.Uri!.AbsolutePath.EndsWith($"/{PhoneNumberId}/messages", StringComparison.Ordinal) && !IsReadReceipt(request.Body));
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal("image", body.RootElement.GetProperty("type").GetString());
        var image = body.RootElement.GetProperty("image");
        Assert.Equal(
            ($"https://public.test/{session.FileId}/catalogo.jpg", "Catálogo de Laura"),
            (image.GetProperty("link").GetString(), image.GetProperty("caption").GetString()));
        Assert.Equal(1L, await BotMessagesAsync(f));
        Assert.Equal(session.FileId, await ScalarAsync<Guid>(connectionString, "SELECT storage_file_id FROM messaging.message_media"));
    }

    // Regla 2 del owner (plan Task 11): la bandeja conserva sus archivos. Ningún chatbot nombra ya el archivo, pero la conversación sigue
    // mostrando la burbuja del bot: para Storage está en uso y el barrido no lo suelta. Para otro tenant no cuenta.
    [Fact]
    public async Task AFileShownInAConversationIsNotReleased()
    {
        await using var database = await StartDatabaseAsync();
        var (f, fileId) = await ArrangeBotSentImageAsync(database);
        using var _ = f;
        Assert.Equal(1, await ExecuteAsync(f.ConnectionString,
            "UPDATE automations.chatbots SET draft_flow = @flow::jsonb WHERE id = @id", ("flow", PlainHandoffFlow), ("id", f.ChatbotId)));
        Assert.Equal(1, await ExecuteAsync(f.ConnectionString,
            "UPDATE automations.chatbot_versions SET flow = @flow::jsonb WHERE chatbot_id = @id", ("flow", PlainHandoffFlow), ("id", f.ChatbotId)));
        var unused = Guid.CreateVersion7();

        await using var scope = f.Host.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<IChatbotMediaReferences>();

        Assert.Equal([fileId], await references.FindReferencedAsync(f.Tenant.TenantId, [fileId, unused], Ct));
        Assert.Empty(await references.FindReferencedAsync(Guid.CreateVersion7(), [fileId], Ct));
    }

    // Regla 2 del owner (plan Task 12): borrar el chatbot avisa a Storage del archivo de su Mensaje, pero la conversación sigue mostrando la
    // burbuja del bot. La pregunta que Storage le hace a Bootstrapper por los ids de ese aviso responde que siguen en uso, así que el
    // archivo se queda.
    [Fact]
    public async Task DeletingTheChatbotKeepsAFileTheInboxShows()
    {
        await using var database = await StartDatabaseAsync();
        var (f, fileId) = await ArrangeBotSentImageAsync(database);
        using var _ = f;

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteChatbotAsync(f)).StatusCode);

        using var payload = JsonDocument.Parse(await ScalarAsync<string>(f.ConnectionString,
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @event", ("event", ChatbotsApiTests.MediaReleasedEvent)));
        var released = payload.RootElement.GetProperty("fileIds").EnumerateArray().Select(element => element.GetGuid()).ToList();
        Assert.Equal([fileId], released);
        await using var scope = f.Host.Services.CreateAsyncScope();
        var references = scope.ServiceProvider.GetRequiredService<IChatbotMediaReferences>();
        Assert.Equal([fileId], await references.FindReferencedAsync(f.Tenant.TenantId, released, Ct));
    }

    private const string PlainHandoffFlow = """
        { "schema": 1, "start": { "firstNodeId": "h1" }, "variables": [],
          "nodes": [ { "id": "h1", "kind": "handoff", "title": "Asesor", "text": null } ] }
        """;

    /// <summary>Un chatbot publicado con <see cref="ImageFlow"/> que ya le mandó la imagen a un cliente: la bandeja tiene la fila de
    /// <c>message_media</c> con el <c>storage_file_id</c>.</summary>
    private static async Task<(Fixture Fixture, Guid FileId)> ArrangeBotSentImageAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var notifier = new RecordingNotifier();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessagingNotifier>();
            services.AddSingleton<IMessagingNotifier>(notifier);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio", PhoneNumberId, "222");
        var session = await factory.Media.OpenUploadAsync(tenant.TenantId, "catalogo.jpg", "image/jpeg", 2048, 25_000_000, Ct);
        await factory.Media.CompleteAsync(tenant.TenantId, session.FileId, Ct);
        using var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var chatbotId = await PublishChatbotAsync(
            admin, tenant.TenantId, ImageFlow.Replace("{fileId}", session.FileId.ToString(), StringComparison.Ordinal), connectionId);
        var f = new Fixture(factory, host, connectionString, tenant, connectionId, chatbotId, host.CreateClient(), notifier, new BotMeta(factory.MetaHandler));
        await InboundAsync(f, "w1", "hola");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal(session.FileId, await ScalarAsync<Guid>(connectionString, "SELECT storage_file_id FROM messaging.message_media"));
        return (f, session.FileId);
    }
}
