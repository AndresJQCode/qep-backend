using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Soporte fase 4b (H10, Q-1), calcado de <see cref="OrderTemplateProvisioningApiTests"/>: la plantilla «Respuesta formal» sube el
/// PDF de ejemplo, crea con el handle y deja una fila PENDING <c>SupportFormalReply</c> con auditoría
/// <c>support_formal_reply_provisioned</c>; repetir es <c>support_formal_reply_exists</c> (un 422 de dominio, como la de «Aviso de
/// respuesta»); la señal devuelve la fila sólo APPROVED; y la pantalla muestra las dos plantillas de Soporte por separado.</summary>
public sealed class SupportFormalReplyTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var meta = factory.MetaHandler;
        meta.RespondWith($"/{MetaAppId}/uploads", _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"upload:MTph"}"""));
        meta.Respond("/upload:MTph", HttpStatusCode.OK, """{"h":"4::handle"}""");
        var created = 770;
        meta.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Post =>
                FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}"""),
            var method when method == HttpMethod.Delete => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}"""),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string FormalUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/support-formal-reply";

    private static MetaRequest[] MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task ProvisionsOnceAndUploadsTheExample()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, FormalUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_soporte_formal", "es", "UTILITY", "PENDING", "SupportFormalReply", "771", "media_header"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString(),
            created.GetProperty("unsupportedReason").GetString()));
        Assert.Equal(SupportFormalReplyTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        var posts = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/100200300/uploads", "/v24.0/upload:MTph", "/v24.0/222/message_templates"], posts.Select(call => call.Uri!.AbsolutePath));
        using var payload = JsonDocument.Parse(posts[2].Body!);
        var components = payload.RootElement.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HEADER", "BODY"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("4::handle", components[0].GetProperty("example").GetProperty("header_handle")[0].GetString());
        Assert.Equal("PENDING|771|qep_soporte_formal", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'SupportFormalReply' AND tenant_id = @t",
            ("t", f.Tenant.TenantId)));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.support_formal_reply_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{FormalUrl(f)}/draft", Ct);
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "radicado", "respuesta"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));
    }

    // El plan dice 409; el código de las plantillas del sistema responde 422 de dominio (MessagingDomainException), y se sigue el código.
    [Fact]
    public async Task ASecondProvisionIs422Exists()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, FormalUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, FormalUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.support_formal_reply_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task ReadinessReturnsTheApprovedRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<ApprovedTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISupportFormalReplyTemplateReadiness>()
                .FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, FormalUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'SupportFormalReply' AND tenant_id = @t", ("t", f.Tenant.TenantId));

        var approved = await ApprovedAsync(f.ConnectionId);

        Assert.Equal(("qep_soporte_formal", "es"), (approved?.Name, approved?.Language));
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));
        using (var scope = f.Factory.Services.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ISupportReplyTemplateReadiness>()
                .FindApprovedAsync(f.Tenant.TenantId, f.ConnectionId, Ct));
        }
    }

    [Fact]
    public async Task TheOverviewShowsBothSupportTemplates()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var reply = await (await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/support-reply"))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var formal = await (await SendAsync(f.Admin, HttpMethod.Post, FormalUrl(f))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())",
            ("t", f.Tenant.TenantId), ("c", f.ConnectionId));

        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal(reply.GetProperty("id").GetGuid(), screen.GetProperty("supportReplyTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(formal.GetProperty("id").GetGuid(), screen.GetProperty("supportFormalReplyTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal("PENDING", screen.GetProperty("supportFormalReplyTemplate").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("supportSurveyTemplate").ValueKind);
    }
}
