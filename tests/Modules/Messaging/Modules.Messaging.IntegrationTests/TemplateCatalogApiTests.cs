using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §5.2 por HTTP y contra la base: <c>GET /connections</c> (las activas del tenant, con la lectura de la
/// bandeja) y <c>GET /connections/{id}/templates</c> (sólo las aprobadas, normalizadas, leídas del espejo local, que se llena con el
/// primer <c>GET</c> y se sincroniza con <c>refresh</c>).</summary>
public sealed class TemplateCatalogApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions));
    }

    private const string TemplatesPage = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"},{"type":"FOOTER","text":"QCode"},
                         {"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"},{"type":"URL","text":"Ver","url":"https://qcode.co"}]}]},
          {"id":"2","name":"borrador","language":"es","status":"REJECTED","category":"MARKETING","components":[{"type":"BODY","text":"x"}]},
          {"id":"3","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Mira"}]}
        ]}
        """;

    private static int TemplateCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    [Fact]
    public async Task TheConnectionsAreTheActiveWhatsAppOnesByNameWithTheirNumber()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Atención", "333", "444");

        var response = await f.Client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var connections = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToArray();
        Assert.Equal(["Atención", "Ventas"], connections.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(f.ConnectionId, connections[1].GetProperty("id").GetGuid());
        Assert.Equal("+57 300 123 4567", connections[1].GetProperty("displayPhoneNumber").GetString());
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnotherTenantOrNoPermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var anotherTenant = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), ReadPermissions);
        using var withoutPermission = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, (await anotherTenant.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutPermission.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections", Ct)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheApprovedTemplatesComeBackNormalizedAndMetaSeesTheRightRequest()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);

        var response = await f.Client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var templates = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToArray();
        Assert.Equal(["promo", "seguimiento"], templates.Select(item => item.GetProperty("name").GetString()));
        var promo = templates[0];
        Assert.False(promo.GetProperty("sendable").GetBoolean());
        Assert.Equal("media_header", promo.GetProperty("unsupportedReason").GetString());
        var follow = templates[1];
        Assert.True(follow.GetProperty("sendable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, follow.GetProperty("unsupportedReason").ValueKind);
        Assert.Equal(("Named", "UTILITY", "es"), (follow.GetProperty("parameterFormat").GetString(), follow.GetProperty("category").GetString(), follow.GetProperty("language").GetString()));
        Assert.Equal("Hola {{first_name}}, ¿sigues interesado?", follow.GetProperty("body").GetString());
        Assert.Equal("QCode", follow.GetProperty("footer").GetString());
        Assert.Equal(JsonValueKind.Null, follow.GetProperty("header").ValueKind);
        Assert.Equal(["first_name"], follow.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["QuickReply|Sí|", "Url|Ver|https://qcode.co"],
            follow.GetProperty("buttons").EnumerateArray().Select(button =>
                $"{button.GetProperty("type").GetString()}|{button.GetProperty("text").GetString()}|{(button.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : string.Empty)}"));
        var request = Assert.Single(f.Factory.MetaHandler.Requests, seen => seen.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));
        Assert.Equal("/v24.0/222/message_templates", request.Uri!.AbsolutePath);
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", request.Authorization);
        Assert.Contains("fields=id,name,language,status,category,components,parameter_format,rejected_reason", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=100", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSecondReadComesFromTheMirrorAndRefreshAsksMetaAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);
        var url = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";

        await f.Client.GetAsync(url, Ct);
        await f.Client.GetAsync(url, Ct);
        Assert.Equal(1, TemplateCalls(f.Factory.MetaHandler));

        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url + "?refresh=true", Ct)).StatusCode);
        Assert.Equal(2, TemplateCalls(f.Factory.MetaHandler));
    }

    // Spec 2026-10-14 D-T8: una vez lleno, el espejo es el catálogo; Meta caída ya no lo vacía ni le pega en cada lectura.
    [Fact]
    public async Task OnceMirroredMetaBeingDownDoesNotEmptyTheCatalog()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);
        var url = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(url, Ct)).StatusCode);
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.BadGateway, "{}");

        var response = await f.Client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["promo", "seguimiento"], (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(1, TemplateCalls(f.Factory.MetaHandler));
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates"));
    }

    [Fact]
    public async Task ARefreshWithOnlyTheReadPermissionIs403AndNeverReachesMeta()
    {
        // R8: refresh sincroniza el espejo contra Meta; leer el catálogo del espejo sigue siendo de lectura.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);
        var url = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates";

        var refreshed = await f.Client.GetAsync(url + "?refresh=true", Ct);
        var plain = await f.Client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refreshed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(1, TemplateCalls(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs422ConnectionUnavailableAndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await f.Client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{Guid.CreateVersion7()}/templates", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.connection_unavailable", (await ProblemAsync(response)).Code);
        Assert.Equal(0, TemplateCalls(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AListMetaDoesNotAnswerIs422TemplatesUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.BadGateway, "{}");

        var response = await f.Client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/connections/{f.ConnectionId}/templates", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.templates.unavailable", (await ProblemAsync(response)).Code);
    }
}
