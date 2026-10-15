using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.5 por HTTP y contra la base, con el reintento versionado (enmienda del owner, 2026-10-15): sube el PDF, crea
/// con el handle, deja fila PENDING Quotation con auditoría; repetir es quotation_exists; una REJECTED se recrea como la versión siguiente y
/// la vieja se borra en Meta después de crear; un reintento que Meta rechaza no borra nada; name_taken salta de versión; una externa igual se
/// adopta sin tocar Meta; una externa distinta es quotation_conflict; la subida fallida es upload_failed sin fila; el nombre reservado no se
/// puede usar en una libre. Y la señal de §5: devuelve el nombre y el idioma de la Quotation sólo cuando está APPROVED.</summary>
public sealed class QuotationTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private const string QuotationBody = "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.";

    // El mismo cuerpo de error que TemplateManagementCreateApiTests y TemplateGraphErrorsTests traducen a name_taken.
    private const string NameTakenError =
        """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Message Template Name Already Exists","error_user_msg":"x","fbtrace_id":"t"}}""";

    private static string ExternalQuotation(string body = QuotationBody, string headerFormat = "DOCUMENT") =>
        $$$"""
        {"data":[{"id":"42","name":"qep_cotizacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
          "components":[{"type":"HEADER","format":"{{{headerFormat}}}","example":{"header_handle":["x"]}},
                        {"type":"BODY","text":"{{{body}}}","example":{"body_text_named_params":[{"param_name":"fullname","example":"Juan"}]}},
                        {"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]}]}
        """;

    /// <param name="create">La respuesta de la creación número n (desde 1); <c>null</c> = la normal, con id 771, 772… por cada éxito.</param>
    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, string listing = """{"data":[]}""", bool uploadFails = false, Func<int, HttpResponseMessage?>? create = null)
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
        // Cada creación exitosa devuelve un id distinto (771, 772…), como Meta: el reintento crea la 772 y borra la 771.
        var attempts = 0;
        var created = 770;
        meta.RespondWith("/222/message_templates", request => request.Method switch
        {
            var method when method == HttpMethod.Post => create?.Invoke(Interlocked.Increment(ref attempts))
                ?? FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}"""),
            var method when method == HttpMethod.Delete => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}"""),
            _ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, listing),
        });
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string QuotationUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/quotation";

    private static MetaRequest[] MetaCalls(Fixture f) => f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    private static bool IsCreate(MetaRequest call) => call.Method == HttpMethod.Post && call.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal);

    private static string CreatedName(MetaRequest call)
    {
        using var payload = JsonDocument.Parse(call.Body!);
        return payload.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<string> QuotationRowAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || meta_template_id || '|' || name FROM messaging.message_templates WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId));

    [Fact]
    public async Task ProvisioningUploadsThePdfThenCreatesWithTheHandleAndStoresAPendingQuotationTemplate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_cotizacion", "es", "UTILITY", "PENDING", "Quotation", "771", false, "media_header"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString(),
            created.GetProperty("sendable").GetBoolean(), created.GetProperty("unsupportedReason").GetString()));
        Assert.Equal(["fullname", "order_number", "total", "valid_until"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), created.GetProperty("createdBy").GetProperty("memberId").GetGuid());
        var calls = MetaCalls(f).Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(["/v24.0/100200300/uploads", "/v24.0/upload:MTph", "/v24.0/222/message_templates"], calls.Select(call => call.Uri!.AbsolutePath));
        Assert.Equal("OAuth " + SentinelMetaAccessToken, calls[1].Authorization);
        using var payload = JsonDocument.Parse(calls[2].Body!);
        Assert.Equal("qep_cotizacion", payload.RootElement.GetProperty("name").GetString());
        var header = payload.RootElement.GetProperty("components").EnumerateArray().First();
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Quotation' AND status = 'PENDING' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));

        // El marcador de sincronización hace que la pantalla lea el espejo en vez de sincronizar contra un listado vacío que borraría la fila.
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal("PENDING", screen.GetProperty("quotationTemplate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProvisioningTwiceIs422QuotationExistsWithoutTouchingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.quotation_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task ARejectedQuotationTemplateIsRecreatedAsTheNextVersionAndTheOldOneDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calls = MetaCalls(f);
        var creates = calls.Where(IsCreate).ToArray();
        Assert.Equal(["qep_cotizacion", "qep_cotizacion_v2"], creates.Select(CreatedName));
        var delete = Assert.Single(calls, call => call.Method == HttpMethod.Delete);
        Assert.Equal("?hsm_id=771&name=qep_cotizacion", delete.Uri!.Query);
        // Crear antes de borrar: si Meta no acepta la nueva, la rechazada sigue en Meta y en el espejo.
        Assert.True(Array.IndexOf(calls, delete) > Array.IndexOf(calls, creates[1]));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal("PENDING|772|qep_cotizacion_v2", await QuotationRowAsync(f));
    }

    [Fact]
    public async Task AFailedRetryKeepsTheRejectedRowAndDeletesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, create: attempt => attempt == 2
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(100))
            : null);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.rejected_by_meta", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain(MetaCalls(f), call => call.Method == HttpMethod.Delete);
        Assert.Equal("REJECTED|771|qep_cotizacion", await QuotationRowAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ANameTakenInMetaMovesToTheNextVersion()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, create: attempt => attempt == 1
            ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, NameTakenError)
            : null);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("qep_cotizacion_v2", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());
        var calls = MetaCalls(f);
        Assert.Equal(["qep_cotizacion", "qep_cotizacion_v2"], calls.Where(IsCreate).Select(CreatedName));
        Assert.Single(calls, call => call.Uri!.AbsolutePath == "/v24.0/100200300/uploads");
        Assert.Single(calls, call => call.Uri!.AbsolutePath == "/v24.0/upload:MTph");
        Assert.Equal("PENDING|771|qep_cotizacion_v2", await QuotationRowAsync(f));
    }

    [Fact]
    public async Task AnExternalTemplateWithTheSameBodyIsAdoptedWithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalQuotation());
        using var host = f.Factory;
        // La pantalla llena el espejo con la externa.
        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("quotationTemplate").ValueKind);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var adopted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Quotation", "APPROVED", "42", "qep_cotizacion"), (
            adopted.GetProperty("kind").GetString(), adopted.GetProperty("status").GetString(), adopted.GetProperty("metaTemplateId").GetString(), adopted.GetProperty("name").GetString()));
        Assert.Equal(before, MetaCalls(f).Length);
        // §4.5 «adopted = true»: la marca de la adopción es que cambió el kind.
        Assert.Equal("[\"kind\"]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned' AND resource_id = @id",
            ("id", adopted.GetProperty("id").GetGuid().ToString())));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned'"));
    }

    [Fact]
    public async Task AnExternalTemplateWithAnotherBodyIs422QuotationConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, ExternalQuotation(body: "Otro texto {{fullname}} {{order_number}} {{total}} {{valid_until}}"));
        using var host = f.Factory;
        await f.Admin.GetAsync(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.quotation_conflict", (await ProblemAsync(response)).Code);
        Assert.Equal("External", await ScalarAsync<string>(f.ConnectionString, "SELECT kind FROM messaging.message_templates WHERE name = 'qep_cotizacion' AND tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AFailedUploadIs422UploadFailedAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, uploadFails: true);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.upload_failed", (await ProblemAsync(response)).Code);
        Assert.DoesNotContain(MetaCalls(f), IsCreate);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.quotation_provisioned'"));
    }

    [Fact]
    public async Task WithoutThePermissionIs403AndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(manager, HttpMethod.Post, QuotationUrl(f));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, MetaCalls(f).Length);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AFreeTemplateCannotTakeTheReservedName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates", new
        {
            name = "qep_cotizacion_v9",
            language = "es",
            category = "UTILITY",
            body = "Hola, ya abrimos.",
            examples = new Dictionary<string, string>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "name"), (code, Assert.Single(keys)));
    }

    [Fact]
    public async Task TheReadinessPortReturnsTheApprovedQuotationTemplateOfThatConnectionOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        async Task<ApprovedQuotationTemplate?> ApprovedAsync(Guid connectionId)
        {
            using var scope = f.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IQuotationTemplateReadiness>()
                .FindApprovedAsync(f.Tenant.TenantId, connectionId, Ct);
        }

        Task ApproveAsync() =>
            ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'APPROVED' WHERE kind = 'Quotation' AND tenant_id = @t", ("t", f.Tenant.TenantId));

        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ApproveAsync();
        Assert.Equal(new ApprovedQuotationTemplate("qep_cotizacion", "es"), await ApprovedAsync(f.ConnectionId));
        // Otra conexión del mismo tenant no está lista.
        Assert.Null(await ApprovedAsync(Guid.CreateVersion7()));

        // El nombre sale de la fila, no de una constante: tras el reintento versionado, la aprobada es qep_cotizacion_v2.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.message_templates SET status = 'REJECTED', status_reason = 'INVALID_FORMAT' WHERE tenant_id = @t", ("t", f.Tenant.TenantId));
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, QuotationUrl(f))).StatusCode);
        Assert.Null(await ApprovedAsync(f.ConnectionId));
        await ApproveAsync();
        Assert.Equal(new ApprovedQuotationTemplate("qep_cotizacion_v2", "es"), await ApprovedAsync(f.ConnectionId));
    }
}
