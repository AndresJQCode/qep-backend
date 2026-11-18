using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Mayorista, calcado de <see cref="SupportReplyTemplateProvisioningApiTests"/>: aprovisionar por conexión crea sólo con cuerpo y deja
/// fila PENDING <c>WholesaleInvitationNotice</c> con auditoría <c>wholesale_invitation_notice_provisioned</c>, que la pantalla muestra en
/// <c>wholesaleInvitationNoticeTemplate</c>; repetir es <c>wholesale_invitation_notice_exists</c>; una REJECTED se recrea como la versión
/// siguiente; una externa igual se adopta; el borrador trae el texto base con sus seis variables; y la señal devuelve la fila sólo cuando
/// está APPROVED y sólo de esa conexión.</summary>
public sealed class WholesaleInvitationNoticeTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string NoticeBody = "Hola {{nombre_contacto}}, {{mayorista}} te invitó como distribuidor. Revisa el correo {{correo}}, por favor.";

    private static string ExternalNotice(string body = NoticeBody) =>
        $$$"""
        {"data":[{"id":"42","name":"qep_mayorista_invitacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
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

    private static string NoticeUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/wholesale-invitation-notice";

    private static MetaRequest[] MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    private static bool IsCreate(MetaRequest call) =>
        call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal);

    private static string CreatedName(MetaRequest call)
    {
        using var payload = JsonDocument.Parse(call.Body!);
        return payload.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<string> NoticeRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'WholesaleInvitationNotice' AND tenant_id = @t",
            ("t", f.Tenant.TenantId));

    [Fact]
    public async Task ProvisioningCreatesTheBodyOnlyTemplateAndTheScreenShowsItPending()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_mayorista_invitacion", "es", "UTILITY", "PENDING", "WholesaleInvitationNotice", "771"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(["nombre_contacto", "mayorista", "correo"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(WholesaleInvitationNoticeTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        // Sin encabezado ni botones: no se sube ningún ejemplo, sólo se crea.
        var posts = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/222/message_templates"], posts.Select(call => call.Uri!.AbsolutePath));
        using var payload = JsonDocument.Parse(posts[0].Body!);
        Assert.Equal(["BODY"], payload.RootElement.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("PENDING|771|qep_mayorista_invitacion", await NoticeRowAsync(f));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.wholesale_invitation_notice_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("wholesaleInvitationNoticeTemplate").GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("wholesaleInvitationNoticeTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("supportReplyTemplate").ValueKind);
    }

    [Fact]
    public async Task ProvisioningTwiceIsWholesaleInvitationNoticeExistsWithoutTouchingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        // La de «Aviso de respuesta» viva en la misma conexión: su índice único parcial no cuenta la del aviso, ni al revés.
        using (var scope = f.Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.MessageTemplates.Add(MessageTemplate.Pending(
                f.Tenant.TenantId, f.ConnectionId, "500", SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.Language,
                SupportReplyTemplateDefinition.Category, TemplateComponentsBuilder.Named, MessageTemplateStatuses.Approved,
                TemplateComponentsBuilder.Components(
                    SupportReplyTemplateDefinition.Draft(SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.BaseBody)).ToJsonString(),
                MessageTemplateKind.SupportReply, null, DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.wholesale_invitation_notice_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal("SupportReply,WholesaleInvitationNotice", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(kind, ',' ORDER BY kind) FROM messaging.message_templates WHERE tenant_id = @t AND connection_id = @c",
            ("t", f.Tenant.TenantId), ("c", f.ConnectionId)));
    }

    [Fact]
    public async Task ARejectedOneIsRecreatedAsTheNextVersion()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = MetaCalls(f);
        Assert.Equal(["qep_mayorista_invitacion", "qep_mayorista_invitacion_v2"], calls.Where(IsCreate).Select(CreatedName));
        var delete = Assert.Single(calls, call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_mayorista_invitacion", delete.Uri!.Query);
        Assert.Equal("PENDING|772|qep_mayorista_invitacion_v2", await NoticeRowAsync(f));
    }

    [Fact]
    public async Task AnExternalWithTheSameBodyIsAdoptedAndWithAnotherBodyIsConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalNotice());
        using var host = f.Factory;
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("wholesaleInvitationNoticeTemplate").ValueKind);

        var conflict = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f), new { body = "Otro texto {{nombre_contacto}}." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("messaging.template.wholesale_invitation_notice_conflict", (await ProblemAsync(conflict)).Code);

        var before = MetaCalls(f).Length;
        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f), new { body = NoticeBody });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("WholesaleInvitationNotice", "APPROVED", "42", "qep_mayorista_invitacion"), (
            adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString(),
            adopted.GetProperty("name").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal("""["kind"]""", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.wholesale_invitation_notice_provisioned' AND resource_id = @id",
            ("id", adopted.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task TheDraftRouteAnswersTheBaseTextWithItsSixVariables()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{NoticeUrl(f)}/draft", Ct);

        Assert.Equal(WholesaleInvitationNoticeTemplateDefinition.BaseBody, draft.GetProperty("body").GetString());
        Assert.False(draft.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("rejectionReason").ValueKind);
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "mayorista", "correo"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f), new { body = "Hola {{nombre_contacto}}, pedido {{numero_pedido}}." });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "body"), (code, Assert.Single(keys)));
        Assert.DoesNotContain(MetaCalls(f), IsCreate);
    }

    [Fact]
    public async Task WithoutThePermissionTheRoutesAre403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var baseUrl = TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{NoticeUrl(f)}/draft", Ct)).StatusCode);

        // El nombre es reservado: una plantilla libre no puede tomar una versión del aviso.
        var free = await SendAsync(f.Admin, HttpMethod.Post, $"{baseUrl}/templates", new
        {
            name = "qep_mayorista_invitacion_v3",
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
    public async Task TheReadinessPortReturnsTheApprovedRowOfThatConnectionOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var otherConnectionId = Guid.CreateVersion7();

        async Task<ApprovedTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IWholesaleInvitationNoticeReadiness>()
                .FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'WholesaleInvitationNotice' AND tenant_id = @t", ("t", f.Tenant.TenantId));

        var approved = await ApprovedAsync(f.ConnectionId);

        Assert.Equal(("qep_mayorista_invitacion", "es", "UTILITY"), (approved?.Name, approved?.Language, approved?.Category));
        Assert.Contains("{{mayorista}}", approved!.ComponentsJson, StringComparison.Ordinal);
        Assert.Null(await ApprovedAsync(otherConnectionId));
    }
}
