using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Automations.Api;
using Modules.Automations.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-18: subir el archivo de un bloque «Mensaje» por la API. Lo autoriza <c>automations.chatbot.manage</c> solo (nunca
/// <c>storage.file.*</c>), y un id de archivo de otro tenant es <c>not_found</c> sobre <c>fileId</c>. Storage es un doble
/// (<see cref="FakeChatbotMediaStore"/>); la política es la real, la de Mensajería.
/// </summary>
public sealed class ChatbotMediaApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ChatbotsUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/chatbots";

    private static string SessionUrl(Guid tenantId, Guid chatbotId) => $"{ChatbotsUrl(tenantId)}/{chatbotId}/media/upload-session";

    [Fact]
    public async Task TheSessionNeedsOnlyChatbotManage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenant, chatbotId) = await TenantWithChatbotAsync(factory, database.GetConnectionString());
        using var manager = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AutomationsPermissions.ChatbotManage);
        using var reader = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AutomationsPermissions.ChatbotRead);
        var body = new CreateChatbotMediaSessionRequest("catalogo.png", "image/png", 1024);

        using var allowed = await manager.PostAsJsonAsync(SessionUrl(tenant.TenantId, chatbotId), body, Ct);
        using var denied = await reader.PostAsJsonAsync(SessionUrl(tenant.TenantId, chatbotId), body, Ct);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var session = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync(Ct));
        Assert.NotEqual(Guid.Empty, session.RootElement.GetProperty("fileId").GetGuid());
        Assert.StartsWith("https://", session.RootElement.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("image/png", session.RootElement.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ACompleteOfAnotherTenantsFileIsNotFoundOnFileId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (owner, ownerChatbotId) = await TenantWithChatbotAsync(factory, database.GetConnectionString());
        var (other, otherChatbotId) = await TenantWithChatbotAsync(factory, database.GetConnectionString());
        using var ownerClient = CreateClient(factory, owner.OwnerUserId, owner.TenantId, ManagePermissions);
        using var otherClient = CreateClient(factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        using var opened = await ownerClient.PostAsJsonAsync(
            SessionUrl(owner.TenantId, ownerChatbotId), new CreateChatbotMediaSessionRequest("catalogo.png", "image/png", 1024), Ct);
        var fileId = (await opened.Content.ReadFromJsonAsync<ChatbotMediaSessionDto>(Ct))!.FileId;

        using var response = await otherClient.PostAsync(
            $"{ChatbotsUrl(other.TenantId)}/{otherChatbotId}/media/{fileId}/complete", content: null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("automations.media.not_found", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("fileId", out _));
    }

    private static async Task<(RegisteredTenant Tenant, Guid ChatbotId)> TenantWithChatbotAsync(
        QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        using var created = await client.PostAsJsonAsync(
            ChatbotsUrl(tenant.TenantId),
            new CreateChatbotRequest("Catálogo", "Manda el catálogo.", "NewConversation", [], [], new ChatbotSourceRequest("blank", null, null, null)),
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (tenant, (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!.Chatbot.Id);
    }
}
