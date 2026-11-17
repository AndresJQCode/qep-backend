using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
