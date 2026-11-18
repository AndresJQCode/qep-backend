using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Messaging.Application;
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
}
