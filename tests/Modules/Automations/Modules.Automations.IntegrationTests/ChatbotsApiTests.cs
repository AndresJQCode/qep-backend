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

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(missing)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, draftMissing.StatusCode);
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
            Assert.Equal(FlowFormatException.UnknownKind, problem.RootElement.GetProperty("errors").GetProperty("flow")[0].GetString());
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
        Assert.Equal(("validation.failed", "flow"), await CodeAndKeyAsync(tooBig));
        Assert.Equal(editor.Chatbot.Version, (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Version);
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
        Assert.Equal(HttpStatusCode.OK, (await SaveDraftAsync(setup, editor, flow.ToJsonString())).StatusCode);
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var response = await reader.PostAsync($"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/validate", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issues = (await response.Content.ReadFromJsonAsync<List<FlowIssueDto>>(Ct))!;
        var reloaded = await GetAsync(setup, editor.Chatbot.Id);
        Assert.NotEmpty(issues);
        Assert.Equal(reloaded.Issues, issues);
        // Sin efectos: ni versión nueva ni auditoría.
        Assert.Equal(reloaded.Chatbot.Version, (await GetAsync(setup, editor.Chatbot.Id)).Chatbot.Version);
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

    private static async Task<(string? Code, string Key)> CodeAndKeyAsync(HttpResponseMessage response)
    {
        var (code, keys) = await ProblemAsync(response);
        return (code, Assert.Single(keys));
    }
}
