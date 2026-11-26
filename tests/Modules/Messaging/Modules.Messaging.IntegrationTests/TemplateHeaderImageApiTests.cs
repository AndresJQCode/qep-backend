using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-17: la imagen fija del encabezado de una plantilla de WhatsApp, por HTTP. La sesión de subida la autoriza sólo
/// <c>messaging.template.manage</c>, nunca <c>storage.file.*</c>.</summary>
public sealed class TemplateHeaderImageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SessionUrl(Guid tenantId, Guid connectionId) =>
        $"{TemplateManagementUrl(tenantId, connectionId)}/header-image/upload-session";

    [Fact]
    public async Task TheSessionIsOpenWithTemplateManageOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        // Sólo messaging.template.manage: sin storage.file.upload la sesión se abre igual.
        using var templateAdmin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions);
        using var manager = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var body = new { fileName = "banner.png", mimeType = "image/png", sizeBytes = 1024 };

        var response = await SendAsync(templateAdmin, HttpMethod.Post, SessionUrl(tenant.TenantId, connectionId), body);
        var forbidden = await SendAsync(manager, HttpMethod.Post, SessionUrl(tenant.TenantId, connectionId), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var fileId = session.GetProperty("fileId").GetGuid();
        Assert.Equal(tenant.TenantId, factory.TemplateImages.Files[fileId].TenantId);
        Assert.StartsWith("https://", session.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        var headers = session.GetProperty("headers");
        Assert.Equal("image/png", headers.GetProperty("Content-Type").GetString());
        Assert.Equal("*", headers.GetProperty("If-None-Match").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Single(factory.TemplateImages.Files);
    }

    private static string ReplaceUrl(Guid tenantId, Guid connectionId, Guid templateId) =>
        $"{TemplateManagementUrl(tenantId, connectionId)}/templates/{templateId}/header-image";

    private static async Task<Guid> SeedImageTemplateAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, Guid currentFileId)
    {
        var now = DateTimeOffset.UtcNow;
        var template = MessageTemplate.Pending(
            tenantId, connectionId, "900", "promo_imagen", "es", "MARKETING", "NAMED", "APPROVED",
            """[{"type":"HEADER","format":"IMAGE","example":{"header_handle":["https://scontent.whatsapp.net/x"]}},{"type":"BODY","text":"Hola"}]""",
            MessageTemplateKind.Free, null, now);
        template.AttachHeaderImage(currentFileId, $"https://public.test/{currentFileId}.png", now);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>().Add(template);
        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        return template.Id;
    }

    // Spec 2026-11-20: dos cambios seguidos. Cada respuesta trae la URL nueva y el formato; las dos anteriores quedan retiradas y nada se libera.
    [Fact]
    public async Task ReplacingTheHeaderImageReturnsTheNewUrlAndKeepsTheOld()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var original = Guid.CreateVersion7();
        var templateId = await SeedImageTemplateAsync(factory, tenant.TenantId, connectionId, original);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions);
        var first = factory.TemplateImages.Seed(tenant.TenantId, [1, 2, 3]);
        var second = factory.TemplateImages.Seed(tenant.TenantId, [4, 5, 6]);

        var firstResponse = await SendAsync(client, HttpMethod.Put, ReplaceUrl(tenant.TenantId, connectionId, templateId), new { headerImageFileId = first });
        var secondResponse = await SendAsync(client, HttpMethod.Put, ReplaceUrl(tenant.TenantId, connectionId, templateId), new { headerImageFileId = second });

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal($"https://public.test/{second}.png", body.GetProperty("headerImageUrl").GetString());
        Assert.Equal("Image", body.GetProperty("headerMedia").GetString());
        Assert.Equal(2, await CountAsync(connectionString, "SELECT count(*) FROM messaging.message_template_retired_header_images"));
        Assert.All(factory.TemplateImages.Files.Values, file => Assert.False(file.Released));
    }

    [Fact]
    public async Task ReplacingWithoutPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var templateId = await SeedImageTemplateAsync(factory, tenant.TenantId, connectionId, Guid.CreateVersion7());
        using var manager = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var fileId = factory.TemplateImages.Seed(tenant.TenantId, [1, 2, 3]);

        var response = await SendAsync(manager, HttpMethod.Put, ReplaceUrl(tenant.TenantId, connectionId, templateId), new { headerImageFileId = fileId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync(connectionString, "SELECT count(*) FROM messaging.message_template_retired_header_images"));
    }
}
