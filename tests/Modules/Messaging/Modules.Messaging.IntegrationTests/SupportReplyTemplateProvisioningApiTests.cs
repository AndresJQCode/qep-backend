using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Soporte fase 2, calcado de <see cref="OrderTemplateProvisioningApiTests"/> sin el PDF de ejemplo: aprovisionar por conexión crea
/// sólo con cuerpo y deja fila PENDING SupportReply con auditoría <c>support_reply_provisioned</c>, que la pantalla muestra en
/// <c>supportReplyTemplate</c>; repetir es support_reply_exists; una REJECTED se recrea como la versión siguiente; una externa igual se
/// adopta; el borrador trae el texto base con sus seis variables; y la señal devuelve la fila sólo cuando está APPROVED.</summary>
public sealed class SupportReplyTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string SupportBody = "Hola {{nombre_contacto}}, sobre tu caso {{radicado}}: {{respuesta}}. Quedamos atentos.";

    private static string ExternalSupportReply(string body = SupportBody) =>
        $$$"""
        {"data":[{"id":"42","name":"qep_soporte_respuesta","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
          "components":[{"type":"BODY","text":"{{{body}}}","example":{"body_text_named_params":[{"param_name":"nombre_contacto","example":"Juan"}]}}]}]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string listing = """{"data":[]}""")
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
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string SupportReplyUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/support-reply";

    private static MetaRequest[] MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    private static bool IsCreate(MetaRequest call) =>
        call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal);

    private static string CreatedName(MetaRequest call)
    {
        using var payload = JsonDocument.Parse(call.Body!);
        return payload.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<string> SupportReplyRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'SupportReply' AND tenant_id = @t",
            ("t", f.Tenant.TenantId));

    [Fact]
    public async Task ProvisioningCreatesTheBodyOnlyTemplateAndStoresAPendingRowThatTheScreenShows()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_soporte_respuesta", "es", "UTILITY", "PENDING", "SupportReply", "771"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(["nombre_contacto", "radicado", "respuesta"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(SupportReplyTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        // Sin encabezado: no se sube ningún ejemplo, sólo se crea.
        var posts = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/222/message_templates"], posts.Select(call => call.Uri!.AbsolutePath));
        using var payload = JsonDocument.Parse(posts[0].Body!);
        Assert.Equal(["BODY"], payload.RootElement.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("PENDING|771|qep_soporte_respuesta", await SupportReplyRowAsync(f));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.support_reply_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("supportReplyTemplate").GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("supportReplyTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("orderTemplate").ValueKind);
    }

    [Fact]
    public async Task ProvisioningTwiceIsSupportReplyExistsWithoutTouchingMetaAndDoesNotCollideWithTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        // La de pedido viva en la misma conexión: su índice único parcial no cuenta la de Soporte, ni al revés.
        using (var scope = f.Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.MessageTemplates.Add(MessageTemplate.Pending(
                f.Tenant.TenantId, f.ConnectionId, "500", OrderTemplateDefinition.Name, OrderTemplateDefinition.Language, OrderTemplateDefinition.Category,
                TemplateComponentsBuilder.Named, MessageTemplateStatuses.Approved,
                TemplateComponentsBuilder.Components(OrderTemplateDefinition.Draft(OrderTemplateDefinition.Name, OrderTemplateDefinition.BaseBody), TemplateMediaHeader.Document("4::handle")).ToJsonString(),
                MessageTemplateKind.Order, null, DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.support_reply_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal("Order,SupportReply", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(kind, ',' ORDER BY kind) FROM messaging.message_templates WHERE tenant_id = @t AND connection_id = @c",
            ("t", f.Tenant.TenantId), ("c", f.ConnectionId)));
    }

    [Fact]
    public async Task ARejectedSupportReplyTemplateIsRecreatedAsTheNextVersionAndTheOldOneDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = MetaCalls(f);
        Assert.Equal(["qep_soporte_respuesta", "qep_soporte_respuesta_v2"], calls.Where(IsCreate).Select(CreatedName));
        var delete = Assert.Single(calls, call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_soporte_respuesta", delete.Uri!.Query);
        Assert.Equal("PENDING|772|qep_soporte_respuesta_v2", await SupportReplyRowAsync(f));
    }

    [Fact]
    public async Task AnExternalSupportReplyWithTheSameBodyIsAdoptedAndWithAnotherBodyIsSupportReplyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalSupportReply());
        using var host = f.Factory;
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("supportReplyTemplate").ValueKind);

        var conflict = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f), new { body = "Otro texto {{nombre_contacto}}." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("messaging.template.support_reply_conflict", (await ProblemAsync(conflict)).Code);

        var before = MetaCalls(f).Length;
        var response = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f), new { body = SupportBody });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("SupportReply", "APPROVED", "42", "qep_soporte_respuesta"), (
            adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString(),
            adopted.GetProperty("name").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task TheDraftRouteAnswersTheBaseTextWithTheSupportVariablesAndAVariableOutsideTheListIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{SupportReplyUrl(f)}/draft", Ct);

        Assert.Equal(SupportReplyTemplateDefinition.BaseBody, draft.GetProperty("body").GetString());
        Assert.False(draft.GetProperty("locked").GetBoolean());
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "radicado", "respuesta"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        var response = await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f), new { body = "Hola {{nombre_contacto}}, pedido {{numero_pedido}}." });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "body"), (code, Assert.Single(keys)));
        Assert.DoesNotContain(MetaCalls(f), IsCreate);
    }

    [Fact]
    public async Task WithoutThePermissionTheRoutesAre403AndAFreeTemplateCannotTakeTheReservedName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var baseUrl = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, SupportReplyUrl(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{SupportReplyUrl(f)}/draft", Ct)).StatusCode);

        var free = await SendAsync(f.Admin, HttpMethod.Post, $"{baseUrl}/templates", new
        {
            name = "qep_soporte_respuesta_v3",
            language = "es",
            category = "UTILITY",
            body = "Hola, ya abrimos.",
            examples = new Dictionary<string, string>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, free.StatusCode);
        var (code, keys) = await ProblemAsync(free);
        Assert.Equal(("validation.failed", "name"), (code, Assert.Single(keys)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task TheReadinessPortReturnsTheApprovedSupportReplyTemplateOfThatConnectionOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<ApprovedTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISupportReplyTemplateReadiness>().FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, SupportReplyUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'SupportReply' AND tenant_id = @t", ("t", f.Tenant.TenantId));

        var approved = await ApprovedAsync(f.ConnectionId);

        Assert.Equal(("qep_soporte_respuesta", "es"), (approved?.Name, approved?.Language));
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));
    }
}
