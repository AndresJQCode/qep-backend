using System.Net;
using System.Net.Http.Json;
using Modules.Automations.Api;
using Modules.Automations.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.4 (<c>GET /connections</c>) y D-M6: las conexiones del tenant con el chatbot que las atiende; Instagram
/// viaja con su canal para que el formulario la deshabilite.</summary>
public sealed class ConnectionsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Digits() => Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ConnectionsUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/connections";

    [Fact]
    public async Task ListsTheTenantConnectionsWithServedByAndBusinessHours()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        var free = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", Digits(), Digits());
        var instagram = await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Tienda", Digits());
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        await PublishAsync(client, tenant.TenantId, whatsApp);

        var items = await client.GetFromJsonAsync<List<SelectableConnectionDto>>(ConnectionsUrl(tenant.TenantId), Ct);

        Assert.NotNull(items);
        Assert.Equal(3, items.Count);
        var served = items.Single(item => item.Id == whatsApp);
        Assert.Equal("Servicio técnico", served.ServedBy?.Name);
        Assert.Equal("WhatsApp", served.Channel);
        Assert.Null(items.Single(item => item.Id == free).ServedBy);
        var instagramItem = items.Single(item => item.Id == instagram);
        Assert.Equal("Instagram", instagramItem.Channel);
        Assert.Equal("Active", instagramItem.Status);
        Assert.Null(instagramItem.ServedBy);
        // Sin horarios creados, nadie tiene uno: el horario efectivo es «siempre abierto».
        Assert.All(items, item => Assert.Null(item.BusinessHoursName));
    }

    [Fact]
    public async Task TheEditorListsTheSameConnections()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Tienda", Digits());
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await client.PostAsJsonAsync(
            $"{AutomationsUrl(tenant.TenantId)}/chatbots",
            new CreateChatbotRequest("Servicio técnico", "x", "NewConversation", [], [whatsApp], new ChatbotSourceRequest("blank", null, null, null)),
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var editor = (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;

        var fromEditor = (await client.GetFromJsonAsync<ChatbotEditorDto>($"{AutomationsUrl(tenant.TenantId)}/chatbots/{editor.Chatbot.Id}", Ct))!.Connections;
        var fromList = await client.GetFromJsonAsync<List<SelectableConnectionDto>>(ConnectionsUrl(tenant.TenantId), Ct);

        Assert.Equal(fromEditor, fromList);
    }

    [Fact]
    public async Task ReadingRequiresThePermission()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, "something.else");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(ConnectionsUrl(tenant.TenantId), Ct)).StatusCode);
    }

    // Spec 2026-10-21 D-H4: el editor lista las conexiones «API externa» reales del tenant, activas y pausadas, sin la clave.
    [Fact]
    public async Task TheEditorListsTheExternalApiConnections()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        var active = await SeedExternalApiConnectionAsync(factory, tenant.TenantId, tenant.OwnerUserId, "Clientes", "Active");
        var paused = await SeedExternalApiConnectionAsync(factory, tenant.TenantId, tenant.OwnerUserId, "Órdenes", "Paused");
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var created = await client.PostAsJsonAsync(
            $"{AutomationsUrl(tenant.TenantId)}/chatbots",
            new CreateChatbotRequest("Servicio técnico", "x", "NewConversation", [], [whatsApp], new ChatbotSourceRequest("blank", null, null, null)),
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var editor = (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal(
            [(active, "Active"), (paused, "Paused")],
            editor.ExternalApis.Select(api => (api.Id, api.Status)).OrderBy(api => api.Status, StringComparer.Ordinal));
        Assert.DoesNotContain("SENTINEL", await created.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static async Task PublishAsync(HttpClient client, Guid tenantId, Guid connectionId)
    {
        var created = await client.PostAsJsonAsync(
            $"{AutomationsUrl(tenantId)}/chatbots",
            new CreateChatbotRequest("Servicio técnico", "x", "NewConversation", [], [connectionId], new ChatbotSourceRequest("blank", null, null, null)),
            Ct);
        var editor = (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        var url = $"{AutomationsUrl(tenantId)}/chatbots/{editor.Chatbot.Id}";
        var save = new HttpRequestMessage(HttpMethod.Put, $"{url}/draft")
        {
            Content = JsonContent.Create(new SaveDraftRequest(
                System.Text.Json.JsonDocument.Parse(AnnexA.Json).RootElement,
                System.Text.Json.JsonDocument.Parse("""{ "positions": {} }""").RootElement)),
        };
        save.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        var saved = await client.SendAsync(save, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var version = (await saved.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!.Version;
        var publish = new HttpRequestMessage(HttpMethod.Post, $"{url}/publish");
        publish.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(publish, Ct)).StatusCode);
    }
}
