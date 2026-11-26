using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-21 por HTTP y contra la base, calcado de <see cref="QuotationTemplateProvisioningApiTests"/>: sube el
/// PDF de ejemplo de la factura, crea con el handle, deja fila PENDING Invoice con auditoría <c>invoice_provisioned</c> y la pantalla la muestra en
/// <c>invoiceTemplate</c>; repetir es invoice_exists; una REJECTED se recrea como la versión siguiente; una externa igual se adopta sin tocar
/// Meta; una externa distinta es invoice_conflict; el borrador trae el texto base con sus seis variables; el nombre reservado no se puede usar
/// en una libre; y la señal de D5 devuelve la Invoice sólo cuando está APPROVED.</summary>
public sealed class InvoiceTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string InvoiceBody = "Hola {{nombre_contacto}}, tu factura {{numero_factura}} por {{valor_total}} va adjunto.";

    private static string ExternalInvoice(string body = InvoiceBody) =>
        $$$"""
        {"data":[{"id":"42","name":"qep_factura","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
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

    private static string InvoiceUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/invoice";

    private static MetaRequest[] MetaCalls(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    private static bool IsCreate(MetaRequest call) => call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal);

    private static string CreatedName(MetaRequest call)
    {
        using var payload = JsonDocument.Parse(call.Body!);
        return payload.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<string> InvoiceRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'Invoice' AND tenant_id = @t", ("t", f.Tenant.TenantId));

    [Fact]
    public async Task ProvisioningUploadsThePdfThenCreatesWithTheHandleAndStoresAPendingInvoiceTemplateThatTheScreenShows()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_factura", "es", "UTILITY", "PENDING", "Invoice", "771", "media_header"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString(),
            created.GetProperty("unsupportedReason").GetString()));
        Assert.Equal(["nombre_contacto", "numero_factura", "valor_total"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(InvoiceTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        var calls = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/100200300/uploads", "/v24.0/upload:MTph", "/v24.0/222/message_templates"], calls.Select(call => call.Uri!.AbsolutePath));
        using var payload = JsonDocument.Parse(calls[2].Body!);
        Assert.Equal("qep_factura", payload.RootElement.GetProperty("name").GetString());
        var header = payload.RootElement.GetProperty("components").EnumerateArray().First();
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal("PENDING|771|qep_factura", await InvoiceRowAsync(f));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.invoice_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        // La pantalla la muestra aparte de la de cotización, que sigue en null.
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("invoiceTemplate").GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("invoiceTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
    }

    [Fact]
    public async Task ProvisioningTwiceIs422InvoiceExistsWithoutTouchingMetaAndDoesNotCollideWithTheQuotation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f))).StatusCode);
        // La de cotización convive con la de factura en la misma conexión.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation")).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.invoice_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task ARejectedInvoiceTemplateIsRecreatedAsTheNextVersionAndTheOldOneDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = MetaCalls(f);
        Assert.Equal(["qep_factura", "qep_factura_v2"], calls.Where(IsCreate).Select(CreatedName));
        var delete = Assert.Single(calls, call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_factura", delete.Uri!.Query);
        Assert.Equal("PENDING|772|qep_factura_v2", await InvoiceRowAsync(f));
    }

    [Fact]
    public async Task AnExternalInvoiceTemplateWithTheSameBodyIsAdoptedAndWithAnotherBodyIsInvoiceConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalInvoice());
        using var host = f.Factory;
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("invoiceTemplate").ValueKind);

        var conflict = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f), new { body = "Otro texto {{nombre_contacto}}." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("messaging.template.invoice_conflict", (await ProblemAsync(conflict)).Code);

        var before = MetaCalls(f).Length;
        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f), new { body = InvoiceBody });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Invoice", "APPROVED", "42", "qep_factura"), (
            adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString(), adopted.GetProperty("name").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal("[\"kind\"]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.invoice_provisioned' AND resource_id = @id",
            ("id", adopted.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task TheDraftRouteAnswersTheBaseTextWithTheInvoiceVariablesAndAVariableOutsideTheListIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/invoice/draft", Ct);

        Assert.Equal(InvoiceTemplateDefinition.BaseBody, draft.GetProperty("body").GetString());
        Assert.False(draft.GetProperty("locked").GetBoolean());
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "numero_factura", "valor_total"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f), new { body = "Hola {{nombre_contacto}}, vence {{vigencia}}." });

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

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, InvoiceUrl(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{baseUrl}/invoice/draft", Ct)).StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));

        var free = await SendAsync(f.Admin, HttpMethod.Post, $"{baseUrl}/templates", new
        {
            name = "qep_factura_v3",
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

        var response = await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.upload_failed", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain(MetaCalls(f), IsCreate);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    // La señal de la factura mira sólo la fila Invoice; la Quotation aprobada de la misma conexión no cuenta.
    [Fact]
    public async Task TheInvoiceReadinessPortReturnsTheApprovedInvoiceTemplateOfThatConnectionOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<ApprovedTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IInvoiceTemplateReadiness>().FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation")).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId));
        Assert.Null(await ApprovedAsync(f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, InvoiceUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Invoice' AND tenant_id = @t", ("t", f.Tenant.TenantId));
        var approved = await ApprovedAsync(f.ConnectionId);
        Assert.Equal(("qep_factura", "es"), (approved?.Name, approved?.Language));
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));
    }
}
