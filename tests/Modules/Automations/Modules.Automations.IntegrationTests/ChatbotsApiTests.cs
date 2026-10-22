using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.4, D-C2, D-C3 y D-M6: lista, creación en blanco, edición y borrador del chatbot por la API, con las
/// fronteras de tenant, permiso, capacidad y versión.</summary>
public sealed class ChatbotsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid WhatsAppId);

    // Los ids de Meta son numéricos (IntegrationConnection los valida); únicos por prueba porque las rutas son globales.
    private static string Digits() => Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ChatbotsUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/chatbots";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions.Length == 0 ? ManagePermissions : permissions);
        return new Setup(factory, connectionString, tenant, client, whatsApp);
    }

    private static CreateChatbotRequest Blank(string name, params Guid[] connectionIds) =>
        new(name, "Atiende la línea de servicio.", "NewConversation", [], connectionIds, new ChatbotSourceRequest("blank", null, null, null));

    private static async Task<ChatbotEditorDto> CreateAsync(Setup setup, string name = "Servicio técnico")
    {
        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank(name, setup.WhatsAppId), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> PutAsync(Setup setup, string url, object body, long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return setup.Client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SaveDraftAsync(Setup setup, ChatbotEditorDto editor, string flowJson, string layoutJson = """{ "positions": { "n1": { "x": 10, "y": 20 } } }""") =>
        PutAsync(
            setup,
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/draft",
            new SaveDraftRequest(JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse(layoutJson).RootElement),
            editor.Chatbot.Version);

    private static async Task<ChatbotEditorDto> GetAsync(Setup setup, Guid chatbotId) =>
        (await setup.Client.GetFromJsonAsync<ChatbotEditorDto>($"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}", Ct))!;

    private static bool SameJson(JsonElement actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(expected));

    [Fact]
    public async Task ListIsEmptyForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString(), ReadPermissions);
        using var _ = setup.Client;

        var response = await setup.Client.GetAsync(ChatbotsUrl(setup.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<ChatbotListResponse>(Ct))!.Items);
    }

    [Fact]
    public async Task CreateBlankReturnsTheEditorWithAnEmptyFlowAndStartIssue()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;

        var editor = await CreateAsync(setup);

        Assert.Equal(("Servicio técnico", "Draft", "NewConversation"), (editor.Chatbot.Name, editor.Chatbot.Status, editor.Chatbot.Trigger));
        Assert.Equal(await ScalarAsync<long>(connectionString, "SELECT version FROM automations.chatbots WHERE id = @id", ("id", editor.Chatbot.Id)), editor.Chatbot.Version);
        Assert.True(SameJson(editor.Draft.Flow, """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }"""));
        Assert.True(SameJson(editor.Draft.Layout, """{ "positions": {} }"""));
        Assert.Equal(await OwnerMembershipIdAsync(connectionString, setup.Tenant), editor.Draft.UpdatedBy);
        Assert.Null(editor.Published);
        Assert.Equal(("reiniciar", 3), (editor.RestartKeyword, editor.MaxAttempts));
        var issue = Assert.Single(editor.Issues);
        Assert.Equal(("Error", FlowIssueCodes.StartNoFirstNode), (issue.Severity, issue.Code));
        var connection = Assert.Single(editor.Chatbot.Connections);
        Assert.Equal((setup.WhatsAppId, "Servicio", "WhatsApp", "+57 300 123 4567"), (connection.Id, connection.Name, connection.Channel, connection.Handle));
        var selectable = Assert.Single(editor.Connections);
        Assert.Equal((setup.WhatsAppId, "Active", (ServedByDto?)null), (selectable.Id, selectable.Status, selectable.ServedBy));
        Assert.Equal(["nombre", "telefono", "empresa", "conexion"], editor.Variables);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.created'", ("id", editor.Chatbot.Id.ToString())));
    }

    // D-M6: Instagram queda fuera del motor; la conexión de otro tenant no existe para éste.
    [Fact]
    public async Task CreateWithAnInstagramConnectionIs422ChannelNotSupported()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var instagram = await SeedInstagramConnectionAsync(factory, setup.Tenant.TenantId, "Instagram", Digits());

        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank("Servicio", setup.WhatsAppId, instagram), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("automations.chatbot.channel_not_supported", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots"));
    }

    [Fact]
    public async Task CreateWithAConnectionOfAnotherTenantIs404ConnectionNotFound()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        var other = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var __ = other.Client;

        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank("Servicio", other.WhatsAppId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("automations.connection.not_found", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task CreateWithoutTheModuleIs403ModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        // Mensajería sigue prendida: sólo falta automations.
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "automations");

        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank("Servicio", setup.WhatsAppId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task CreateWithoutManageIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString, ReadPermissions);
        using var _ = setup.Client;

        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank("Servicio", setup.WhatsAppId), Ct);

        // La política de la ruta responde antes que el handler, sin cuerpo (como en Pipeline); nada se crea.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots"));
    }

    [Fact]
    public async Task CreateWithADuplicatedNameIs409NameTaken()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        await CreateAsync(setup, "Servicio técnico");

        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), Blank(" SERVICIO TÉCNICO "), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("automations.chatbot.name_taken", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task GetFromAnotherTenantIs403NotFound()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;
        var editor = await CreateAsync(setupA);

        var underForeignRoute = await b.GetAsync($"{ChatbotsUrl(setupA.Tenant.TenantId)}/{editor.Chatbot.Id}", Ct);
        var underOwnRoute = await b.GetAsync($"{ChatbotsUrl(setupB.Tenant.TenantId)}/{editor.Chatbot.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(underForeignRoute)).Code);
        // Bajo su propia ruta, el chatbot de otro tenant simplemente no existe.
        Assert.Equal(HttpStatusCode.NotFound, underOwnRoute.StatusCode);
        Assert.Equal("automations.chatbot.not_found", (await ProblemAsync(underOwnRoute)).Code);
    }

    [Fact]
    public async Task UpdateWithoutIfMatchIs428AndStaleIs412()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}";
        var body = new UpdateChatbotRequest("Otro nombre", "", "Keyword", ["hola"], "menu", 2, [setup.WhatsAppId]);

        var missing = await PutAsync(setup, url, body, null);
        var stale = await PutAsync(setup, url, body, editor.Chatbot.Version + 5);
        var draftMissing = await PutAsync(setup, $"{url}/draft", new SaveDraftRequest(editor.Draft.Flow, editor.Draft.Layout), null);
        var draftStale = await PutAsync(setup, $"{url}/draft", new SaveDraftRequest(editor.Draft.Flow, editor.Draft.Layout), editor.Chatbot.Version + 5);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(missing)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, draftMissing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, draftStale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(draftStale)).Code);
        Assert.Equal("Servicio técnico", (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Name);
    }

    [Fact]
    public async Task UpdateReplacesTheSettingsAndTheConnections()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var second = await SeedWhatsAppConnectionAsync(factory, setup.Tenant.TenantId, "Ventas", Digits(), Digits());
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}";

        var response = await PutAsync(
            setup, url, new UpdateChatbotRequest(" Ventas ", "Atiende ventas.", "Keyword", ["Menú", "menu", "Hola"], "Volver", 5, [second]),
            editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal(("Ventas", "Atiende ventas.", "Keyword"), (updated.Chatbot.Name, updated.Chatbot.Description, updated.Chatbot.Trigger));
        Assert.Equal(["menu", "hola"], updated.Keywords);
        Assert.Equal(("volver", 5), (updated.RestartKeyword, updated.MaxAttempts));
        Assert.Equal([second], updated.Chatbot.Connections.Select(connection => connection.Id));
        Assert.True(updated.Chatbot.Version > editor.Chatbot.Version);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.updated'", ("id", editor.Chatbot.Id.ToString())));
    }

    // D-M6 también en la edición; y los límites que el agregado da por validados (1..5, 300) nunca llegan a él.
    [Fact]
    public async Task UpdateRejectsInstagramAndOutOfRangeSettings()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var instagram = await SeedInstagramConnectionAsync(factory, setup.Tenant.TenantId, "Instagram", Digits());
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}";

        var withInstagram = await PutAsync(
            setup, url, new UpdateChatbotRequest("Servicio", "", "NewConversation", [], "reiniciar", 3, [instagram]), editor.Chatbot.Version);
        var outOfRange = await PutAsync(
            setup, url, new UpdateChatbotRequest("Servicio", new string('a', 301), "Never", [], "reiniciar", 6, []), editor.Chatbot.Version);
        var zero = await PutAsync(
            setup, url, new UpdateChatbotRequest("Servicio", "", "NewConversation", [], "reiniciar", 0, []), editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withInstagram.StatusCode);
        Assert.Equal("automations.chatbot.channel_not_supported", (await ProblemAsync(withInstagram)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOfRange.StatusCode);
        var (code, keys) = await ProblemAsync(outOfRange);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["Description", "MaxAttempts", "Trigger"], keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["MaxAttempts"], (await ProblemAsync(zero)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(editor.Chatbot.Version, (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Version);
    }

    [Fact]
    public async Task SaveDraftStoresTheAnnexAFlowAndReportsOnlyWarnings()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        const string layout = """{ "positions": { "n1": { "x": 10, "y": 20 } } }""";

        var response = await SaveDraftAsync(setup, editor, AnnexA.Json, layout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.NotEmpty(saved.Issues);
        Assert.All(saved.Issues, issue => Assert.Equal("Warning", issue.Severity));
        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        // D-C2: el servidor guarda el flujo en su forma (la del modelo) y el editor dibuja lo que vuelve.
        Assert.True(SameJson(reloaded.Draft.Flow, FlowJson.Serialize(FlowJson.Parse(AnnexA.Json))));
        Assert.True(SameJson(reloaded.Draft.Layout, layout));
        Assert.Equal(saved.Version, reloaded.Chatbot.Version);
        Assert.Equal(saved.UpdatedAt, reloaded.Draft.UpdatedAt);
        Assert.Equal(saved.Issues, reloaded.Issues);
        Assert.Equal([AnnexA.CustomersApi, AnnexA.ServiceOrdersApi], reloaded.ExternalApis.Select(api => api.Id));
        // Las fijas, las declaradas y las que escriben los bloques.
        Assert.Equal(
            ["nombre", "telefono", "empresa", "conexion", "cedula", "equipos", "cliente", "equipo", "motivo", "factura", "direccion", "orden"],
            reloaded.Variables);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.draft_saved'", ("id", editor.Chatbot.Id.ToString())));
    }

    // D-C3: guardar nunca falla por un flujo a medias.
    [Fact]
    public async Task SaveDraftWithErrorsStillSaves()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var flow = JsonNode.Parse(AnnexA.Json)!;
        flow["nodes"]![0]!["outputs"]!["next"] = null;

        var response = await SaveDraftAsync(setup, editor, flow.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.Contains(saved.Issues, issue => issue is { Severity: "Error", Code: FlowIssueCodes.NodeOutputUnconnected, NodeId: "n1" });
        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        Assert.Equal(JsonValueKind.Null, reloaded.Draft.Flow.GetProperty("nodes")[0].GetProperty("outputs").GetProperty("next").ValueKind);
        Assert.Contains(reloaded.Issues, issue => issue.Code == FlowIssueCodes.NodeOutputUnconnected);
    }

    [Fact]
    public async Task SaveDraftRejectsUnknownKindAndOversizedFlow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        const string unknownKind = """{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "teleport", "title": "x", "outputs": {} } ] }""";
        var oversized = $$"""{ "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [], "nodes": [ { "id": "n1", "kind": "message", "title": "x", "text": "{{new string('a', 600_000)}}", "outputs": { "next": null } } ] }""";

        var unknown = await SaveDraftAsync(setup, editor, unknownKind);
        var tooBig = await SaveDraftAsync(setup, editor, oversized);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(("validation.failed", "flow"), await CodeAndKeyAsync(unknown));
        using (var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct)))
        {
            // El código primero (lo que el editor traduce) y el detalle del parser después, para diagnosticar.
            var flowErrors = problem.RootElement.GetProperty("errors").GetProperty("flow");
            Assert.Equal(2, flowErrors.GetArrayLength());
            Assert.Equal(FlowFormatException.UnknownKind, flowErrors[0].GetString());
            Assert.Contains("teleport", flowErrors[1].GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
        Assert.Equal(("validation.failed", "flow"), await CodeAndKeyAsync(tooBig));
        Assert.Equal(editor.Chatbot.Version, (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Version);
    }

    // §4.4: el layout no se valida (D-C2) pero tiene tope; y los topes se miden en bytes UTF-8, no en caracteres.
    [Fact]
    public async Task SaveDraftRejectsAnOversizedLayoutAndMeasuresInBytes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var bigLayout = $$"""{ "positions": {}, "note": "{{new string('a', 270_000)}}" }""";
        // 200 000 «ñ» son 200 000 caracteres pero 400 000 bytes: pasa el tope de 256 KB sólo si se cuentan caracteres.
        var multiByteLayout = $$"""{ "positions": {}, "note": "{{new string('ñ', 200_000)}}" }""";

        var tooBig = await SaveDraftAsync(setup, editor, ChatbotLimits.BlankFlow, bigLayout);
        // Crudo y sin escapar: JsonContent escribiría cada «ñ» como \u00f1 y la prueba ya no distinguiría bytes de caracteres.
        var multiByte = await SendDraftBodyAsync(
            setup, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/draft",
            new StringContent($$"""{ "flow": {{ChatbotLimits.BlankFlow}}, "layout": {{multiByteLayout}} }""", System.Text.Encoding.UTF8, "application/json"),
            editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
        Assert.Equal(("validation.failed", "layout"), await CodeAndKeyAsync(tooBig));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, multiByte.StatusCode);
        Assert.Equal(("validation.failed", "layout"), await CodeAndKeyAsync(multiByte));
        Assert.Equal(editor.Chatbot.Version, (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Version);
    }

    // El tope del cuerpo corta antes de leerlo entero: declarado de más (Content-Length) o sin declarar (chunked).
    [Fact]
    public async Task SaveDraftWithABodyOverTheCapIs413()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/draft";
        var body = $$"""{ "flow": {{ChatbotLimits.BlankFlow}}, "layout": { "positions": {}, "note": "{{new string('a', 900_000)}}" } }""";

        var declared = await SendDraftBodyAsync(setup, url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), editor.Chatbot.Version);
        var chunked = await SendDraftBodyAsync(setup, url, new UnknownLengthContent(body), editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.draft_saved'", ("id", editor.Chatbot.Id.ToString())));
    }

    // Un borrador guardado que ya no se lee (dato viejo, cambio de esquema) abre el editor con un error en vez de un 500.
    [Fact]
    public async Task AStoredFlowThatNoLongerParsesIsAnIssueNotA500()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        await ExecuteAsync(connectionString,
            """UPDATE automations.chatbots SET draft_flow = '{"schema": 2, "start": {}, "nodes": []}'::jsonb WHERE id = @id""", ("id", editor.Chatbot.Id));

        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        var validate = await setup.Client.PostAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/validate", null, Ct);

        var issue = Assert.Single(reloaded.Issues);
        Assert.Equal(("Error", FlowFormatException.Malformed, (string?)null), (issue.Severity, issue.Code, issue.NodeId));
        Assert.Equal(["nombre", "telefono", "empresa", "conexion"], reloaded.Variables);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.Equal(reloaded.Issues, (await validate.Content.ReadFromJsonAsync<List<FlowIssueDto>>(Ct))!);
    }

    [Fact]
    public async Task ValidateReturnsTheSameIssuesAsGet()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var flow = JsonNode.Parse(AnnexA.Json)!;
        flow["nodes"]![0]!["outputs"]!["next"] = null;
        var save = await SaveDraftAsync(setup, editor, flow.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = (await save.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        const string auditSql = "SELECT count(*) FROM audit.entries WHERE resource_id = @id";
        var auditBefore = await CountAsync(connectionString, auditSql, ("id", editor.Chatbot.Id.ToString()));

        var response = await reader.PostAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/validate", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issues = (await response.Content.ReadFromJsonAsync<List<FlowIssueDto>>(Ct))!;
        Assert.NotEmpty(issues);
        Assert.Equal(saved.Issues, issues);
        // Sin efectos: ni versión nueva ni auditoría.
        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        Assert.Equal(reloaded.Issues, issues);
        Assert.Equal(saved.Version, reloaded.Chatbot.Version);
        Assert.Equal(auditBefore, await CountAsync(connectionString, auditSql, ("id", editor.Chatbot.Id.ToString())));
    }

    [Fact]
    public async Task ListShowsHasDraftChangesAndZeroToday()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        await CreateAsync(setup, "Ventas");

        var all = (await setup.Client.GetFromJsonAsync<ChatbotListResponse>(ChatbotsUrl(setup.Tenant.TenantId), Ct))!;
        var searched = (await setup.Client.GetFromJsonAsync<ChatbotListResponse>($"{ChatbotsUrl(setup.Tenant.TenantId)}?search=técn", Ct))!;

        Assert.Equal(["Servicio técnico", "Ventas"], all.Items.Select(item => item.Name));
        var item = Assert.Single(searched.Items);
        Assert.Equal((editor.Chatbot.Id, "Draft", true, new ChatbotTodayDto(0, 0)), (item.Id, item.Status, item.HasDraftChanges, item.Today));
        Assert.Equal(((int?)null, (DateTimeOffset?)null), (item.PublishedVersion, item.PublishedAt));
        Assert.Equal([setup.WhatsAppId], item.Connections.Select(connection => connection.Id));
    }

    // D-C3: los errores bloquean y viajan en errors["flow"] como «código: mensaje».
    [Fact]
    public async Task PublishAnInvalidDraftIs422ChatbotInvalidWithTheIssues()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);

        var response = await ActAsync(setup.Client, setup, editor.Chatbot.Id, "publish", editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("automations.chatbot.invalid", problem.RootElement.GetProperty("code").GetString());
        var flow = problem.RootElement.GetProperty("errors").GetProperty("flow").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.NotEmpty(flow);
        Assert.Contains(flow, item => item.StartsWith($"{FlowIssueCodes.StartNoFirstNode}: ", StringComparison.Ordinal));
        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        Assert.Equal(("Draft", editor.Chatbot.Version), (reloaded.Chatbot.Status, reloaded.Chatbot.Version));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbot_versions"));
    }

    // Ruling P2-R8: el disparador por palabra clave sin palabras es un problema de los ajustes; los ajustes se guardan, publicar no.
    [Fact]
    public async Task PublishAKeywordChatbotWithoutKeywordsIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var settings = await PutAsync(
            setup, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}",
            new UpdateChatbotRequest("Servicio técnico", "", "Keyword", [], "volver", 3, [setup.WhatsAppId]), version);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var saved = await GetAsync(setup, id);

        var response = await ActAsync(setup.Client, setup, id, "publish", saved.Chatbot.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("automations.chatbot.invalid", problem.RootElement.GetProperty("code").GetString());
        var flow = problem.RootElement.GetProperty("errors").GetProperty("flow").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Contains(flow, item => item.StartsWith($"{FlowIssueCodes.TriggerKeywordsEmpty}: ", StringComparison.Ordinal));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbot_versions"));
    }

    [Fact]
    public async Task PublishTheAnnexAFlowActivatesAndNumbersVersionOne()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);

        var response = await ActAsync(setup.Client, setup, id, "publish", version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal(("Active", false), (published.Chatbot.Status, published.Chatbot.HasDraftChanges));
        Assert.Equal(1, published.Published!.Number);
        Assert.Equal((int?)1, published.Chatbot.PublishedVersion);
        Assert.True(published.Chatbot.Version > version);
        Assert.Equal(new ServedByDto(id, "Servicio técnico"), Assert.Single(published.Connections).ServedBy);
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.published'", ("id", id.ToString())));
    }

    // D-C4: la conexión la ocupa el primero; el 409 nombra al que la tiene.
    [Fact]
    public async Task PublishASecondChatbotOnTheSameConnectionIs409()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(setup.Client, setup, first, "publish", firstVersion)).StatusCode);

        var response = await ActAsync(setup.Client, setup, second, "publish", secondVersion);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("automations.chatbot.connection_taken", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("Servicio técnico", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var reloaded = await GetAsync(setup, second);
        Assert.Equal(("Draft", secondVersion), (reloaded.Chatbot.Status, reloaded.Chatbot.Version));
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
    }

    // El índice parcial, no un chequeo previo, decide entre dos publicaciones simultáneas.
    [Fact]
    public async Task ConcurrentPublishOnTheSameConnectionLeavesOneActive()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        using var clientA = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ManagePermissions);
        using var clientB = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ManagePermissions);

        var responses = await Task.WhenAll(
            ActAsync(clientA, setup, first, "publish", firstVersion),
            ActAsync(clientB, setup, second, "publish", secondVersion));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(response => response.StatusCode).Order());
        var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("automations.chatbot.connection_taken", (await ProblemAsync(conflict)).Code);
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
    }

    // D-C13: la pausa conserva la conexión; D-C5: reanudar vuelve a Active.
    [Fact]
    public async Task PauseKeepsTheConnectionAndResumeWorks()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, first, "publish", firstVersion));

        var pause = await ActAsync(setup.Client, setup, first, "pause", published.Chatbot.Version);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var paused = (await pause.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        var other = await ActAsync(setup.Client, setup, second, "publish", secondVersion);
        var resume = await ActAsync(setup.Client, setup, first, "resume", paused.Chatbot.Version);

        Assert.Equal("Paused", paused.Chatbot.Status);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("automations.chatbot.connection_taken", (await ProblemAsync(other)).Code);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        var resumed = (await resume.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal(("Active", 1), (resumed.Chatbot.Status, resumed.Published!.Number));
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
        foreach (var action in new[] { "paused", "resumed" })
        {
            Assert.Equal(1L, await CountAsync(connectionString,
                "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = @action",
                ("id", first.ToString()), ("action", $"automations.chatbot.{action}")));
        }
    }

    [Fact]
    public async Task PauseADraftIs422NotPublished()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);

        var response = await ActAsync(setup.Client, setup, editor.Chatbot.Id, "pause", editor.Chatbot.Version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("automations.chatbot.not_published", (await ProblemAsync(response)).Code);
    }

    // D-C12: «(copia)», luego «(copia 2)»; la copia nace en borrador y sin conexiones.
    [Fact]
    public async Task DuplicateNumbersTheCopy()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var settings = await PutAsync(
            setup, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}",
            new UpdateChatbotRequest("Servicio técnico", "Atiende la línea.", "Keyword", ["hola"], "volver", 5, [setup.WhatsAppId]), version);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var original = await GetAsync(setup, id);
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}/duplicate";

        var first = await setup.Client.PostAsync(url, null, Ct);
        var second = await setup.Client.PostAsync(url, null, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var copy = (await first.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        var copy2 = (await second.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal("Servicio técnico (copia)", copy.Chatbot.Name);
        Assert.Equal("Servicio técnico (copia 2)", copy2.Chatbot.Name);
        Assert.NotEqual(id, copy.Chatbot.Id);
        Assert.Equal(("Draft", "Atiende la línea.", "Keyword"), (copy.Chatbot.Status, copy.Chatbot.Description, copy.Chatbot.Trigger));
        Assert.Empty(copy.Chatbot.Connections);
        Assert.Null(copy.Published);
        Assert.Equal(["hola"], copy.Keywords);
        Assert.Equal(("volver", 5), (copy.RestartKeyword, copy.MaxAttempts));
        Assert.True(SameJson(copy.Draft.Flow, original.Draft.Flow.GetRawText()));
        Assert.True(SameJson(copy.Draft.Layout, original.Draft.Layout.GetRawText()));
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM automations.chatbot_connections WHERE chatbot_id = @id", ("id", copy.Chatbot.Id)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.created'", ("id", copy.Chatbot.Id.ToString())));
    }

    [Fact]
    public async Task DeleteWithLiveRunsIs409()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));
        var versionId = await ScalarAsync<Guid>(connectionString, "SELECT published_version_id FROM automations.chatbots WHERE id = @id", ("id", id));
        await ExecuteAsync(connectionString,
            """
            INSERT INTO automations.conversation_runs (id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id, status, started_at, updated_at)
            VALUES (@id, @tenantId, @conversationId, @connectionId, @chatbotId, @versionId, 'Waiting', now(), now())
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", setup.Tenant.TenantId), ("conversationId", Guid.CreateVersion7()),
            ("connectionId", setup.WhatsAppId), ("chatbotId", id), ("versionId", versionId));

        var response = await DeleteAsync(setup, id, published.Chatbot.Version);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("automations.chatbot.has_live_runs", (await ProblemAsync(response)).Code);
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
    }

    // D-C13: borrar sí libera la conexión; versiones y conexiones se van en cascada.
    [Fact]
    public async Task DeleteFreesTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (first, firstVersion) = await PublishableAsync(setup);
        var (second, secondVersion) = await PublishableAsync(setup, "Ventas");
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, first, "publish", firstVersion));

        var missing = await DeleteAsync(setup, first, null);
        var deleted = await DeleteAsync(setup, first, published.Chatbot.Version);
        var other = await ActAsync(setup.Client, setup, second, "publish", secondVersion);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.GetAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/{first}", Ct)).StatusCode);
        foreach (var table in new[] { "chatbot_versions", "chatbot_connections" })
        {
            Assert.Equal(0L, await CountAsync(connectionString, $"SELECT count(*) FROM automations.{table} WHERE chatbot_id = @id", ("id", first)));
        }

        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.deleted'", ("id", first.ToString())));
    }

    // D-C9: el archivo lleva el borrador y las «API externa» que nombran sus bloques HTTP.
    [Fact]
    public async Task ExportCarriesTheDraftAndTheExternalApis()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, _) = await PublishableAsync(setup);

        var response = await setup.Client.GetAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}/export", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = (await response.Content.ReadFromJsonAsync<ChatbotExportDto>(Ct))!;
        Assert.Equal((1, "Servicio técnico", "NewConversation", "reiniciar", 3), (export.Schema, export.Name, export.Trigger, export.RestartKeyword, export.MaxAttempts));
        Assert.True(SameJson(export.Flow, FlowJson.Serialize(FlowJson.Parse(AnnexA.Json))));
        Assert.Equal([AnnexA.CustomersApi, AnnexA.ServiceOrdersApi], export.ExternalApis.Select(api => api.Id).Order());
        Assert.All(export.ExternalApis, api => Assert.Equal("Active", api.Status));
    }

    [Fact]
    public async Task ImportRecreatesADraftWithTheSameFlow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, _) = await PublishableAsync(setup);
        var export = (await setup.Client.GetFromJsonAsync<ChatbotExportDto>($"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}/export", Ct))!;

        var response = await setup.Client.PostAsJsonAsync(
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/import", new ImportChatbotRequest("Copia", export, null), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var imported = (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal(("Copia", "Draft"), (imported.Chatbot.Name, imported.Chatbot.Status));
        Assert.Empty(imported.Chatbot.Connections);
        var reloaded = await GetAsync(setup, imported.Chatbot.Id);
        Assert.True(SameJson(reloaded.Draft.Flow, FlowJson.Serialize(FlowJson.Parse(AnnexA.Json))));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.imported'", ("id", imported.Chatbot.Id.ToString())));

        var again = await setup.Client.PostAsJsonAsync(
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/import", new ImportChatbotRequest("copia", export, null), Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("automations.chatbot.name_taken", (await ProblemAsync(again)).Code);
    }

    // D-C15: el mapa reescribe la conexión de los bloques HTTP; la que no tiene par queda y Validar la marca.
    [Fact]
    public async Task ImportRemapsHttpConnections()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var apis = FakeExternalApis.WithAnnexA();
        var replacement = Guid.CreateVersion7();
        apis.Items.Add(new ExternalApiRef(replacement, "Clientes del cliente", "Active"));
        using var factory = new QepApiFactory(connectionString, apis);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, _) = await PublishableAsync(setup);
        var export = (await setup.Client.GetFromJsonAsync<ChatbotExportDto>($"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}/export", Ct))!;
        var url = $"{ChatbotsUrl(setup.Tenant.TenantId)}/import";

        var mapped = await setup.Client.PostAsJsonAsync(
            url, new ImportChatbotRequest("Mapeado", export, new Dictionary<Guid, Guid> { [AnnexA.CustomersApi] = replacement }), Ct);

        Assert.Equal(HttpStatusCode.Created, mapped.StatusCode);
        var mappedEditor = (await mapped.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal([replacement, AnnexA.ServiceOrdersApi], HttpConnectionIds(mappedEditor));
        Assert.DoesNotContain(mappedEditor.Issues, issue => issue.Code == FlowIssueCodes.HttpConnectionMissing);

        apis.Items.Clear();
        var unmapped = await setup.Client.PostAsJsonAsync(url, new ImportChatbotRequest("Sin mapa", export, null), Ct);

        Assert.Equal(HttpStatusCode.Created, unmapped.StatusCode);
        var unmappedEditor = (await unmapped.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        Assert.Equal([AnnexA.CustomersApi, AnnexA.ServiceOrdersApi], HttpConnectionIds(unmappedEditor));
        Assert.Contains(unmappedEditor.Issues, issue => issue.Code == FlowIssueCodes.HttpConnectionMissing);
    }

    [Fact]
    public async Task ImportWithABrokenFlowIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, _) = await PublishableAsync(setup);
        var export = (await setup.Client.GetFromJsonAsync<ChatbotExportDto>($"{ChatbotsUrl(setup.Tenant.TenantId)}/{id}/export", Ct))!;
        var flow = JsonNode.Parse(AnnexA.Json)!;
        flow["nodes"]![0]!["kind"] = "teleport";
        var broken = export with { Flow = JsonDocument.Parse(flow.ToJsonString()).RootElement };

        var response = await setup.Client.PostAsJsonAsync(
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/import", new ImportChatbotRequest("Roto", broken, null), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["export.flow"], keys);
        using (var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)))
        {
            var flowErrors = problem.RootElement.GetProperty("errors").GetProperty("export.flow");
            Assert.Equal((FlowFormatException.UnknownKind, 2), (flowErrors[0].GetString(), flowErrors.GetArrayLength()));
        }

        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots WHERE name = 'Roto'"));
    }

    // El tope de importar es 1 MB, no el del borrador: un cuerpo de ~900 KB llega a validarse (422 por el layout) en vez de cortarse
    // con 413, y uno de más de 1 MB sí se corta.
    [Fact]
    public async Task ImportHasItsOwnOneMegabyteBodyCap()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var export = (await setup.Client.GetFromJsonAsync<ChatbotExportDto>($"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/export", Ct))!;
        ImportChatbotRequest WithNote(int length) => new("Pesado", export with
        {
            Layout = JsonDocument.Parse($$"""{ "positions": {}, "note": "{{new string('a', length)}}" }""").RootElement,
        }, null);

        var underCap = await setup.Client.PostAsJsonAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/import", WithNote(900_000), Ct);
        var overCap = await setup.Client.PostAsJsonAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/import", WithNote(1_100_000), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, underCap.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overCap.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots WHERE name = 'Pesado'"));
    }

    private static Guid[] HttpConnectionIds(ChatbotEditorDto editor) =>
        [.. editor.Draft.Flow.GetProperty("nodes").EnumerateArray()
            .Where(node => node.GetProperty("kind").GetString() == "http")
            .Select(node => node.GetProperty("connectionId").GetGuid())];

    /// <summary>Un chatbot con la conexión de WhatsApp y el flujo del Anexo A guardado (sólo avisos con <see cref="FakeExternalApis.WithAnnexA"/>):
    /// listo para publicar. Devuelve su id y la versión vigente.</summary>
    private static async Task<(Guid Id, long Version)> PublishableAsync(Setup setup, string name = "Servicio técnico")
    {
        var editor = await CreateAsync(setup, name);
        var save = await SaveDraftAsync(setup, editor, AnnexA.Json);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        return (editor.Chatbot.Id, (await save.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!.Version);
    }

    /// <summary><c>POST /chatbots/{id}/{action}</c> con <c>If-Match</c> (publish, pause, resume).</summary>
    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Setup setup, Guid chatbotId, string action, long version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}/{action}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> DeleteAsync(Setup setup, Guid chatbotId, long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}");
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return setup.Client.SendAsync(request, Ct);
    }

    private static async Task<ChatbotEditorDto> ReadEditorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
    }

    private static Task<long> ActiveRowsAsync(string connectionString, Guid connectionId) =>
        CountAsync(connectionString, "SELECT count(*) FROM automations.chatbot_connections WHERE connection_id = @id AND active", ("id", connectionId));

    private static Task<HttpResponseMessage> SendDraftBodyAsync(Setup setup, string url, HttpContent content, long version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return setup.Client.SendAsync(request, Ct);
    }

    /// <summary>Un cuerpo sin <c>Content-Length</c>, como uno chunked.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(string body)
        {
            _bytes = System.Text.Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static async Task<(string? Code, string Key)> CodeAndKeyAsync(HttpResponseMessage response)
    {
        var (code, keys) = await ProblemAsync(response);
        return (code, Assert.Single(keys));
    }
}
