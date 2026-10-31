// tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.7: <c>GET /connections/{id}/template-management</c> (la pantalla, con el primer llenado del espejo) y
/// <c>POST …/sync</c>, con sus 403 (sin permiso, otro tenant) y el 422 de una conexión ajena.</summary>
public sealed class TemplateManagementApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    internal const string Listing = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]},
          {"id":"2","name":"borrador","language":"es","status":"REJECTED","category":"MARKETING","parameter_format":"POSITIONAL","rejected_reason":"INVALID_FORMAT",
           "components":[{"type":"BODY","text":"x {{1}}"}]},
          {"id":"3","name":"en_revision","language":"es","status":"PENDING","category":"MARKETING","parameter_format":"NAMED","rejected_reason":"NONE",
           "components":[{"type":"BODY","text":"Pronto"}]}
        ]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string listing = Listing)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request =>
            request.Method == HttpMethod.Get ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing) : FakeMetaGraphHandler.Json(HttpStatusCode.OK, "{}"));
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static int Listings(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Method == HttpMethod.Get && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    [Fact]
    public async Task TheFirstReadFillsTheMirrorAndShowsEveryStatusWithItsReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Ventas", "+57 300 123 4567"), (screen.GetProperty("connection").GetProperty("name").GetString(), screen.GetProperty("connection").GetProperty("displayPhoneNumber").GetString()));
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, screen.GetProperty("lastSyncedAt").ValueKind);
        var templates = screen.GetProperty("templates").EnumerateArray().ToArray();
        Assert.Equal(["borrador", "en_revision", "seguimiento"], templates.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(["REJECTED", "PENDING", "APPROVED"], templates.Select(item => item.GetProperty("status").GetString()));
        Assert.Equal("INVALID_FORMAT", templates[0].GetProperty("statusReason").GetString());
        Assert.Equal(JsonValueKind.Null, templates[1].GetProperty("statusReason").ValueKind);
        Assert.All(templates, item => Assert.Equal("External", item.GetProperty("kind").GetString()));
        Assert.All(templates, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("createdBy").ValueKind));
        Assert.Equal(["first_name"], templates[2].GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Named", templates[2].GetProperty("parameterFormat").GetString());
        Assert.True(templates[2].GetProperty("sendable").GetBoolean());
        Assert.Equal("2", templates[0].GetProperty("metaTemplateId").GetString());
        Assert.DoesNotContain(SentinelMetaAccessToken, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ASecondReadServesTheMirrorAndSyncAsksMetaAgainDroppingWhatIsGone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);
        await f.Admin.GetAsync(url, Ct);
        await f.Admin.GetAsync(url, Ct);
        Assert.Equal(1, Listings(f.Factory.MetaHandler));
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK,
            """{"data":[{"id":"1","name":"seguimiento","language":"es","status":"PAUSED","category":"UTILITY","parameter_format":"NAMED","components":[{"type":"BODY","text":"Hola {{first_name}}"}]}]}""");

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{url}/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Listings(f.Factory.MetaHandler));
        var templates = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("templates").EnumerateArray().ToArray();
        var only = Assert.Single(templates);
        Assert.Equal(("seguimiento", "PAUSED"), (only.GetProperty("name").GetString(), only.GetProperty("status").GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task WithoutThePermissionOrFromAnotherTenantIs403AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), TemplatePermissions);
        var url = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, $"{url}/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(0, Listings(f.Factory.MetaHandler));
    }

    private static Task<int> SetBillingModuleAsync(Fixture f, string status) =>
        ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, status)
            VALUES (@tenantId, 'billing', now(), 'manual', @status)
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = @status, status_changed_at = now()
            """,
            ("tenantId", f.Tenant.TenantId), ("status", status));

    private static int Creations(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    /// <summary>Cobros recurrentes §5: el producto no ofrece lo que el tenant no tiene; sin el módulo de cobros no se crea nada en Meta.</summary>
    [Fact]
    public async Task ProvisioningBillingTemplatesWithTheBillingModuleOffIs403AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SetBillingModuleAsync(f, "inactive");

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/billing");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
        Assert.Equal(0, Creations(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ProvisioningBillingTemplatesWithoutThePermissionIs403AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SetBillingModuleAsync(f, "active");
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await SendAsync(manager, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/billing");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, Creations(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task ProvisioningBillingTemplatesWithTheModuleOnCreatesBothInMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SetBillingModuleAsync(f, "active");
        var next = 0;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.RespondWith("/222/message_templates", request =>
            request.Method == HttpMethod.Get
                ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}""")
                : FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"90{{++next}}","status":"PENDING","category":"UTILITY"}"""));

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/billing");

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, Creations(f.Factory.MetaHandler));
        Assert.Equal(2L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t AND kind IN ('BillingReminder', 'BillingOverdue')", ("t", f.Tenant.TenantId)));
    }

    private static string[] CreatedBodies(FakeMetaGraphHandler meta) =>
        meta.Requests
            .Where(request => request.Method == HttpMethod.Post && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal))
            .Select(request =>
            {
                using var payload = JsonDocument.Parse(request.Body!);
                return payload.RootElement.GetProperty("components").EnumerateArray()
                    .Single(component => component.GetProperty("type").GetString() == "BODY").GetProperty("text").GetString()!;
            })
            .ToArray();

    [Fact]
    public async Task ProvisioningBillingTemplatesSendsTheEditedBodiesToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SetBillingModuleAsync(f, "active");
        var next = 0;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.RespondWith("/222/message_templates", request =>
            request.Method == HttpMethod.Get
                ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}""")
                : FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"90{{++next}}","status":"PENDING","category":"UTILITY"}"""));
        const string reminder = "Hola {{nombre_contacto}}, tu servicio {{servicio}} vence el {{vencimiento}}.";
        const string overdue = "Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobros vencidos.";

        var response = await SendAsync(
            f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/billing", new { reminderBody = reminder, overdueBody = overdue });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal([reminder, overdue], CreatedBodies(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task ABillingBodyWithAVariableOfTheOtherNoticeIs422WithItsKeyAndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SetBillingModuleAsync(f, "active");

        var response = await SendAsync(
            f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/billing",
            new { reminderBody = "Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobros.", overdueBody = (string?)null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "reminderBody"), (code, Assert.Single(keys)));
        Assert.Equal(0, Creations(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs422ConnectionUnavailableNot404()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.connection_unavailable", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task AListingMetaDoesNotAnswerOnTheFirstReadIs422TemplatesUnavailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        f.Factory.MetaHandler.Reset();
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.BadGateway, "{}");

        var response = await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.templates.unavailable", (await ProblemAsync(response)).Code);
    }
}
