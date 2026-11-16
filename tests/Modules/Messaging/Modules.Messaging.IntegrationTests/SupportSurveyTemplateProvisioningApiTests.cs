using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Soporte fase 4b (H16), calcado de <see cref="SupportReplyTemplateProvisioningApiTests"/>: la plantilla de la encuesta se crea
/// sin encabezado y con los tres botones fijos, deja una fila PENDING <c>SupportSurvey</c> con auditoría
/// <c>support_survey_provisioned</c>; repetir es <c>support_survey_exists</c> (422 de dominio, como las demás del sistema); la señal
/// devuelve la fila sólo APPROVED.</summary>
public sealed class SupportSurveyTemplateProvisioningApiTests
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
        var created = 770;
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Post =>
                FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}"""),
            var method when method == HttpMethod.Delete => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}"""),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string SurveyUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/support-survey";

    private static MetaRequest[] MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task ProvisionsOnceWithTheButtons()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, SurveyUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_soporte_encuesta", "es", "UTILITY", "PENDING", "SupportSurvey", "771"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(SupportSurveyTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        Assert.Equal(["Buena", "Regular", "Mala"], created.GetProperty("buttons").EnumerateArray().Select(button => button.GetProperty("text").GetString()));
        var post = Assert.Single(MetaCalls(f), call => call.Method == HttpMethod.Post);
        Assert.Equal("/v24.0/222/message_templates", post.Uri!.AbsolutePath);
        using var payload = JsonDocument.Parse(post.Body!);
        var components = payload.RootElement.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["BODY", "BUTTONS"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal(["QUICK_REPLY", "QUICK_REPLY", "QUICK_REPLY"],
            components[1].GetProperty("buttons").EnumerateArray().Select(button => button.GetProperty("type").GetString()));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.support_survey_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{SurveyUrl(f)}/draft", Ct);
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "radicado"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())",
            ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("supportSurveyTemplate").GetProperty("templateId").GetGuid());
    }

    // El plan dice 409; el código de las plantillas del sistema responde 422 de dominio (MessagingDomainException), y se sigue el código.
    [Fact]
    public async Task ASecondProvisionIs422Exists()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, SurveyUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, SurveyUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.support_survey_exists", (await ProblemAsync(response)).Code);
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
            return await scope.ServiceProvider.GetRequiredService<ISupportSurveyTemplateReadiness>().FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, SurveyUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'SupportSurvey' AND tenant_id = @t", ("t", f.Tenant.TenantId));

        var approved = await ApprovedAsync(f.ConnectionId);

        Assert.Equal(("qep_soporte_encuesta", "es"), (approved?.Name, approved?.Language));
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));
    }
}
