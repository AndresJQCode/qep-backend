using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-09 D1, D3, D4 y D5 por HTTP y contra la base, calcado de <see cref="QuotationTemplateProvisioningApiTests"/>: sube el
/// PDF de ejemplo del pedido, crea con el handle, deja fila PENDING Order con auditoría <c>order_provisioned</c> y la pantalla la muestra en
/// <c>orderTemplate</c>; repetir es order_exists; una REJECTED se recrea como la versión siguiente; una externa igual se adopta sin tocar
/// Meta; una externa distinta es order_conflict; el borrador trae el texto base con las seis variables; el nombre reservado no se puede usar
/// en una libre; y la señal de D5 devuelve la Order sólo cuando está APPROVED.</summary>
public sealed class OrderTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string OrderBody = "Hola {{nombre_contacto}}, tu pedido {{numero_pedido}} por {{valor_total}} va adjunto.";

    private static string ExternalOrder(string body = OrderBody) =>
        $$$"""
        {"data":[{"id":"42","name":"qep_pedido","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
          "components":[{"type":"HEADER","format":"DOCUMENT","example":{"header_handle":["x"]}},
                        {"type":"BODY","text":"{{{body}}}","example":{"body_text_named_params":[{"param_name":"nombre_contacto","example":"Juan"}]}}]}]}
        """;

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string listing = """{"data":[]}""", bool uploadFails = false)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var meta = factory.MetaHandler;
        meta.RespondWith($"/{MetaAppId}/uploads", _ => uploadFails
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100))
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"upload:MTph"}"""));
        meta.Respond("/upload:MTph", HttpStatusCode.OK, """{"h":"4::handle"}""");
        var created = 770;
        meta.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Post =>
                FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}"""),
            var method when method == HttpMethod.Delete => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string OrderUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/order";

    private static MetaRequest[] MetaCalls(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    private static bool IsCreate(MetaRequest call) => call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal);

    private static string CreatedName(MetaRequest call)
    {
        using var payload = JsonDocument.Parse(call.Body!);
        return payload.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<string> OrderRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'Order' AND tenant_id = @t", ("t", f.Tenant.TenantId));

    [Fact]
    public async Task ProvisioningUploadsThePdfThenCreatesWithTheHandleAndStoresAPendingOrderTemplateThatTheScreenShows()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_pedido", "es", "UTILITY", "PENDING", "Order", "771", "media_header"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString(),
            created.GetProperty("unsupportedReason").GetString()));
        Assert.Equal(["nombre_contacto", "numero_pedido", "valor_total"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(OrderTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        var calls = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/100200300/uploads", "/v24.0/upload:MTph", "/v24.0/222/message_templates"], calls.Select(call => call.Uri!.AbsolutePath));
        using var payload = JsonDocument.Parse(calls[2].Body!);
        Assert.Equal("qep_pedido", payload.RootElement.GetProperty("name").GetString());
        var header = payload.RootElement.GetProperty("components").EnumerateArray().First();
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal("PENDING|771|qep_pedido", await OrderRowAsync(f));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.order_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        // D4: la pantalla la muestra aparte de la de cotización, que sigue en null.
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("orderTemplate").GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("orderTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
    }

    [Fact]
    public async Task ProvisioningTwiceIs422OrderExistsWithoutTouchingMetaAndDoesNotCollideWithTheQuotation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f))).StatusCode);
        // D1: la de cotización convive con la de pedido en la misma conexión.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation")).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.order_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task ARejectedOrderTemplateIsRecreatedAsTheNextVersionAndTheOldOneDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = MetaCalls(f);
        Assert.Equal(["qep_pedido", "qep_pedido_v2"], calls.Where(IsCreate).Select(CreatedName));
        var delete = Assert.Single(calls, call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_pedido", delete.Uri!.Query);
        Assert.Equal("PENDING|772|qep_pedido_v2", await OrderRowAsync(f));
    }

    [Fact]
    public async Task AnExternalOrderTemplateWithTheSameBodyIsAdoptedAndWithAnotherBodyIsOrderConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalOrder());
        using var host = f.Factory;
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("orderTemplate").ValueKind);

        var conflict = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f), new { body = "Otro texto {{nombre_contacto}}." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("messaging.template.order_conflict", (await ProblemAsync(conflict)).Code);

        var before = MetaCalls(f).Length;
        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f), new { body = OrderBody });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Order", "APPROVED", "42", "qep_pedido"), (
            adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString(), adopted.GetProperty("name").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal("[\"kind\"]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.order_provisioned' AND resource_id = @id",
            ("id", adopted.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task TheDraftRouteAnswersTheBaseTextWithTheOrderVariablesAndAVariableOutsideTheListIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/order/draft", Ct);

        Assert.Equal(OrderTemplateDefinition.BaseBody, draft.GetProperty("body").GetString());
        Assert.False(draft.GetProperty("locked").GetBoolean());
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "numero_pedido", "valor_total"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f), new { body = "Hola {{nombre_contacto}}, vence {{vigencia}}." });

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

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, OrderUrl(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{baseUrl}/order/draft", Ct)).StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));

        var free = await SendAsync(f.Admin, HttpMethod.Post, $"{baseUrl}/templates", new
        {
            name = "qep_pedido_v3",
            language = "es",
            category = "UTILITY",
            body = "Hola, ya abrimos.",
            examples = new Dictionary<string, string>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, free.StatusCode);
        var (code, keys) = await ProblemAsync(free);
        Assert.Equal(("validation.failed", "name"), (code, Assert.Single(keys)));
    }

    [Fact]
    public async Task AFailedUploadIs422UploadFailedAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, uploadFails: true);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.upload_failed", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain(MetaCalls(f), IsCreate);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    // D5: la señal del pedido mira sólo la fila Order; la Quotation aprobada de la misma conexión no cuenta.
    [Fact]
    public async Task TheOrderReadinessPortReturnsTheApprovedOrderTemplateOfThatConnectionOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<ApprovedTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IOrderTemplateReadiness>().FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation")).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId));
        Assert.Null(await ApprovedAsync(f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, OrderUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Order' AND tenant_id = @t", ("t", f.Tenant.TenantId));
        var approved = await ApprovedAsync(f.ConnectionId);
        Assert.Equal(("qep_pedido", "es"), (approved?.Name, approved?.Language));
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));
    }
}
