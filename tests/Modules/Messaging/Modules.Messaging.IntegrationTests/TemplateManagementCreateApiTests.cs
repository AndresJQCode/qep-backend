using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.3 «CreateTemplateCommand» y §4.8 por HTTP y contra la base: Meta recibe el payload, queda fila PENDING Free
/// con auditoría; el nombre repetido no llega a Meta; un rechazo de Meta es 422 con su texto y no escribe nada; validación por campo; 403.</summary>
public sealed class TemplateManagementCreateApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, Func<HttpRequestMessage, HttpResponseMessage>? onCreate = null, HttpStatusCode uploadStatus = HttpStatusCode.OK)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method == HttpMethod.Post
            ? (onCreate ?? (_ => FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"555","status":"PENDING","category":"MARKETING"}""")))(request)
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}"""));
        // Spec 2026-11-17: la subida de la imagen como ejemplo del encabezado, la misma Resumable Upload del PDF del pedido.
        factory.MetaHandler.RespondWith($"/{MetaAppId}/uploads", _ => uploadStatus == HttpStatusCode.OK
            ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"id":"upload:IMG"}""")
            : FakeMetaGraphHandler.Json(uploadStatus, FakeMetaGraphHandler.GraphError(1)));
        factory.MetaHandler.Respond("/upload:IMG", HttpStatusCode.OK, """{"h":"4::img"}""");
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static object Body(string name = "promo_octubre") => new
    {
        name,
        language = "es",
        category = "MARKETING",
        header = "Oferta de octubre",
        body = "Hola {{first_name}}, este mes {{product}} tiene {{discount}} de descuento.",
        footer = "Responde DETENER para no recibir promociones.",
        buttons = new object[]
        {
            new { type = "QuickReply", text = "Detener promociones" },
            new { type = "Url", text = "Ver catálogo", url = "https://ejemplo.co/catalogo" },
        },
        examples = new Dictionary<string, string> { ["first_name"] = "Ana", ["product"] = "Silla Nova", ["discount"] = "20 %" },
    };

    private static string TemplatesUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/templates";

    private static IEnumerable<MetaRequest> Uploads(FakeMetaGraphHandler meta) =>
        meta.Requests.Where(request => request.Method == HttpMethod.Post && request.Uri!.AbsolutePath.EndsWith($"/{MetaAppId}/uploads", StringComparison.Ordinal));

    /// <summary>Spec 2026-11-17: el cuerpo de crear con la imagen fija y sin encabezado de texto.</summary>
    private static object ImageBody(Guid fileId, string name = "promo_imagen") => new
    {
        name,
        language = "es",
        category = "MARKETING",
        headerImageFileId = fileId,
        body = "Hola {{first_name}}, mira la oferta de este mes.",
        examples = new Dictionary<string, string> { ["first_name"] = "Ana" },
    };

    private static IEnumerable<MetaRequest> Creates(FakeMetaGraphHandler meta) =>
        meta.Requests.Where(request => request.Method == HttpMethod.Post && request.Uri!.AbsolutePath.EndsWith("/222/message_templates", StringComparison.Ordinal));

    [Fact]
    public async Task CreatingPostsToMetaStoresAPendingFreeTemplateAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), response.Headers.Location!.ToString());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("promo_octubre", "es", "MARKETING", "PENDING", "Free", "555"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(["first_name", "product", "discount"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), created.GetProperty("createdBy").GetProperty("memberId").GetGuid());
        var request = Assert.Single(Creates(f.Factory.MetaHandler));
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", request.Authorization);
        using var payload = JsonDocument.Parse(request.Body!);
        Assert.Equal("NAMED", payload.RootElement.GetProperty("parameter_format").GetString());
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"], payload.RootElement.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Free' AND status = 'PENDING' AND name = 'promo_octubre'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = 'messaging.template.created' AND resource_type = 'message_template' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));
        // Spec 2026-11-17: un encabezado de texto sigue igual, sin imagen.
        Assert.Equal(JsonValueKind.Null, created.GetProperty("headerImageUrl").ValueKind);
        Assert.Equal("Oferta de octubre", created.GetProperty("header").GetString());
        Assert.Empty(Uploads(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task ARepeatedNameIsNameTakenWithoutASecondCallToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body())).StatusCode);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.name_taken", (await ProblemAsync(response)).Code);
        Assert.Single(Creates(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task AMetaRejectionIsRejectedByMetaWithItsTextAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Invalid Format","error_user_msg":"The body ends with a variable.","fbtrace_id":"t"}}"""));
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.rejected_by_meta", (await ProblemAsync(response)).Code);
        Assert.Contains("The body ends with a variable.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.template.created'"));
    }

    [Fact]
    public async Task ANameMetaAlreadyHasIsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Message Template Name Already Exists","error_user_msg":"x","fbtrace_id":"t"}}"""));
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal("messaging.template.name_taken", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ValidationMarksEachFieldAndNeverReachesMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), new
        {
            name = "qep_cotizacion",
            language = "es",
            category = "UTILITY",
            body = "Hola {{first_name}}",
            examples = new Dictionary<string, string>(),
            buttons = new object[] { new { type = "Url", text = "Ver", url = "http://inseguro.co" } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["buttons[0].url", "examples", "name"], keys);
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task ABodyWithoutVariablesAndWithoutExamplesIsCreated()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        // «examples» se omite: el DTO lo declara opcional y un cuerpo sin variables no lo necesita.
        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), new { name = "ya_abrimos", language = "es", category = "UTILITY", body = "Hola, ya abrimos." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Free' AND status = 'PENDING' AND name = 'ya_abrimos'"));
    }

    [Fact]
    public async Task WithoutThePermissionIs403AndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await SendAsync(manager, HttpMethod.Post, TemplatesUrl(f), Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }

    [Fact]
    public async Task CreatingWithAnImagePostsAnImageHeaderAndStoresTheUrl()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048]);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), ImageBody(fileId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal($"https://public.test/{fileId}.png", created.GetProperty("headerImageUrl").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("header").ValueKind);
        Assert.Single(Uploads(f.Factory.MetaHandler));
        using var payload = JsonDocument.Parse(Assert.Single(Creates(f.Factory.MetaHandler)).Body!);
        var header = payload.RootElement.GetProperty("components")[0];
        Assert.Equal(("HEADER", "IMAGE", "4::img"), (
            header.GetProperty("type").GetString(), header.GetProperty("format").GetString(),
            header.GetProperty("example").GetProperty("header_handle")[0].GetString()));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.message_templates WHERE name = 'promo_imagen' AND header_image_file_id = @file AND header_image_url = @url",
            ("file", fileId), ("url", $"https://public.test/{fileId}.png")));
        Assert.True(f.Factory.TemplateImages.Files[fileId].Published);
        Assert.False(f.Factory.TemplateImages.Files[fileId].Released);
    }

    [Fact]
    public async Task AnImageOfAnotherTenantIsHeaderImageNotFoundOnTheField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(Guid.CreateVersion7(), new byte[2048]);

        await AssertFileProblemAsync(f, fileId, "messaging.template.header_image_not_found");
    }

    [Fact]
    public async Task AnImageNotYetUploadedIsNotUploaded()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, [], state: TemplateImageState.NotUploaded, size: 2048);

        await AssertFileProblemAsync(f, fileId, "messaging.template.header_image_not_uploaded");
    }

    [Fact]
    public async Task AQuarantinedImageIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048], state: TemplateImageState.Rejected);

        await AssertFileProblemAsync(f, fileId, "messaging.template.header_image_rejected");
    }

    [Fact]
    public async Task AGifIsUnsupported()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048], mimeType: "image/gif");

        await AssertFileProblemAsync(f, fileId, "messaging.template.header_image_unsupported");
    }

    [Fact]
    public async Task ASixMegabyteImageIsTooLarge()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[16], size: 6L * 1024 * 1024);

        await AssertFileProblemAsync(f, fileId, "messaging.template.header_image_too_large");
    }

    [Fact]
    public async Task AMetaRejectionReleasesThePublishedImage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100,"error_user_title":"Invalid Format","error_user_msg":"The image is too small.","fbtrace_id":"t"}}"""));
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048]);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), ImageBody(fileId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.rejected_by_meta", (await ProblemAsync(response)).Code);
        Assert.Single(Creates(f.Factory.MetaHandler));
        // Se publicó y se liberó: el fake deja Published en false y Released en true.
        Assert.True(f.Factory.TemplateImages.Files[fileId].Released);
        Assert.False(f.Factory.TemplateImages.Files[fileId].Published);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AFailedMetaUploadReleasesThePublishedImage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, uploadStatus: HttpStatusCode.InternalServerError);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048]);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), ImageBody(fileId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.upload_failed", (await ProblemAsync(response)).Code);
        Assert.True(f.Factory.TemplateImages.Files[fileId].Released);
        Assert.Empty(Creates(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AHeaderAndAHeaderImageTogetherAreValidationFailedOnHeader()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var fileId = f.Factory.TemplateImages.Seed(f.Tenant.TenantId, new byte[2048]);

        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), new
        {
            name = "promo_imagen",
            language = "es",
            category = "MARKETING",
            header = "Oferta",
            headerImageFileId = fileId,
            body = "Hola, mira la oferta.",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["header"], keys);
        Assert.False(f.Factory.TemplateImages.Files[fileId].Published);
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }

    private static async Task AssertFileProblemAsync(Fixture f, Guid fileId, string code)
    {
        var response = await SendAsync(f.Admin, HttpMethod.Post, TemplatesUrl(f), ImageBody(fileId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (actualCode, keys) = await ProblemAsync(response);
        Assert.Equal(code, actualCode);
        Assert.Equal(["headerImageFileId"], keys);
        Assert.False(f.Factory.TemplateImages.Files[fileId].Published);
        Assert.Empty(Uploads(f.Factory.MetaHandler));
        Assert.Empty(Creates(f.Factory.MetaHandler));
    }
}
