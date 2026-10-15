using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.3 «DeleteTemplateCommand», D-T11, D-T12 y D-T13 por HTTP: Meta recibe hsm_id y name, la fila se elimina y se
/// audita; «no existe» en Meta es 204 igual; 404 para un id ajeno; 422 in_use_by_campaign con una campaña programada, también para la de
/// cotización.</summary>
public sealed class TemplateManagementDeleteApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string Listing = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"POSITIONAL",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu código es {{2}}"}]},
          {"id":"2","name":"aviso","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"POSITIONAL",
           "components":[{"type":"BODY","text":"Tu pedido salió"}]}
        ]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool metaSaysGone = false, Func<HttpResponseMessage>? deleteAnswer = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Delete && deleteAnswer is not null => deleteAnswer(),
            var method when method == HttpMethod.Delete => metaSaysGone
                ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"x","type":"OAuthException","code":100,"error_user_title":"Invalid Template","error_user_msg":"Message template does not exist in the given language.","fbtrace_id":"t"}}""")
                : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, Listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static async Task<Dictionary<string, Guid>> IdsByNameAsync(Fixture f)
    {
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        return screen.GetProperty("templates").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("id").GetGuid(), StringComparer.Ordinal);
    }

    private static string TemplateUrl(Fixture f, Guid templateId) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates/{templateId}";

    private static IEnumerable<MetaRequest> Deletes(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Method == HttpMethod.Delete);

    [Fact]
    public async Task DeletingCallsMetaWithHsmIdAndNameRemovesTheRowAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["promo"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var delete = Assert.Single(Deletes(f));
        Assert.Equal(("/v24.0/222/message_templates", "?hsm_id=1&name=promo", $"Bearer {SentinelMetaAccessToken}"), (delete.Uri!.AbsolutePath, delete.Uri.Query, delete.Authorization));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.deleted' AND resource_id = @id", ("id", ids["promo"].ToString())));
    }

    [Fact]
    public async Task DeletingWhatMetaNoLongerHasIs204AndRemovesTheRowAnyway()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, metaSaysGone: true);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["aviso"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE name = 'aviso' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    /// <summary>Revisión final F6: un borrado que Meta rechaza es <c>rejected_by_meta</c> con el texto de un borrado, no el de la creación; la
    /// fila queda y no se audita.</summary>
    [Fact]
    public async Task AMetaRejectionOfTheDeletionIs422WithTheDeletionTextAndKeepsTheRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, deleteAnswer: () => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Invalid parameter","error_user_msg":"The template is locked by an active review.","fbtrace_id":"t"}}"""));
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["promo"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.rejected_by_meta", (await ProblemAsync(response)).Code);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("WhatsApp no pudo borrar la plantilla: The template is locked by an active review.", body, StringComparison.Ordinal);
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.deleted'"));
    }

    [Fact]
    public async Task AnUnknownTemplateIdIs404AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await IdsByNameAsync(f);

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.template.not_found", (await ProblemAsync(response)).Code);
        Assert.Empty(Deletes(f));
    }

    [Fact]
    public async Task DeletingATemplateUsedByAScheduledCampaignIs422AndKeepsTheRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);
        using var campaigns = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CampaignPermissions);
        var campaignsUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/campaigns";
        var created = await SendAsync(campaigns, HttpMethod.Post, campaignsUrl, new
        {
            name = "Promo",
            connectionId = f.ConnectionId,
            template = new { name = "promo", language = "es" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "customerName" }, ["2"] = new { source = "text", value = "A1" } },
            audience = new { tagIds = new[] { Guid.CreateVersion7() }, customerIds = Array.Empty<Guid>() },
            scheduledAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var launched = await SendAsync(campaigns, HttpMethod.Post, $"{campaignsUrl}/{campaignId}/launch", new { expectedVersion = 1 });
        Assert.True(launched.IsSuccessStatusCode, await launched.Content.ReadAsStringAsync(Ct));

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, ids["promo"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.in_use_by_campaign", (await ProblemAsync(response)).Code);
        Assert.Empty(Deletes(f));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    // Review Focus 5: la regla de campañas aplica también a la plantilla de cotización. Una campaña no puede crearse con ella por la API
    // (encabezado multimedia, no enviable), así que la fila se escribe directo.
    [Fact]
    public async Task DeletingTheQuotationTemplateUsedByAScheduledCampaignIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await IdsByNameAsync(f);
        var quotationId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.message_templates (id, tenant_id, connection_id, meta_template_id, name, language, category, parameter_format, status, components, kind, created_at, updated_at)
            VALUES (@id, @t, @c, '99', 'qep_cotizacion', 'es', 'UTILITY', 'NAMED', 'APPROVED', '[]'::jsonb, 'Quotation', now(), now())
            """, ("id", quotationId), ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category, parameters,
                audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@id, @t, @c, 'x', 'qep_cotizacion', 'es', 'UTILITY', '{}'::jsonb, ARRAY[@tag]::uuid[], ARRAY[]::uuid[], 'Scheduled', @member, now(), now(), 1)
            """, ("id", Guid.CreateVersion7()), ("t", f.Tenant.TenantId), ("c", f.ConnectionId), ("tag", Guid.CreateVersion7()), ("member", await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant)));

        var response = await SendAsync(f.Admin, HttpMethod.Delete, TemplateUrl(f, quotationId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.in_use_by_campaign", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task WithoutThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var ids = await IdsByNameAsync(f);
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Delete, TemplateUrl(f, ids["promo"]))).StatusCode);
        Assert.Empty(Deletes(f));
    }
}
