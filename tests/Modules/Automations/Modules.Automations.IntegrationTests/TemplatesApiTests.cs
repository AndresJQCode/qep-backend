using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 D-C8, D-C14 y §4.4: el catálogo de plantillas (sistema y tenant), guardar un chatbot como plantilla, borrarla
/// y crear un chatbot desde una plantilla o como duplicado.</summary>
public sealed class TemplatesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid WhatsAppId);

    private static string Digits() => Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ChatbotsUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/chatbots";

    private static string TemplatesUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/templates";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, params string[] permissions)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Servicio", Digits(), Digits());
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions.Length == 0 ? ManagePermissions : permissions);
        return new Setup(factory, connectionString, tenant, client, whatsApp);
    }

    private static CreateChatbotRequest Request(string name, ChatbotSourceRequest source, params Guid[] connectionIds) =>
        new(name, "Atiende la línea de servicio.", "NewConversation", [], connectionIds, source);

    private static async Task<ChatbotEditorDto> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
    }

    private static async Task<ChatbotEditorDto> CreateBlankAsync(Setup setup, string name = "Servicio técnico") =>
        await CreatedAsync(await setup.Client.PostAsJsonAsync(
            ChatbotsUrl(setup.Tenant.TenantId), Request(name, new ChatbotSourceRequest("blank", null, null, null), setup.WhatsAppId), Ct));

    private static async Task<long> SaveDraftAsync(Setup setup, ChatbotEditorDto editor, string flowJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/draft")
        {
            Content = JsonContent.Create(new SaveDraftRequest(
                JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse("""{ "positions": { "n1": { "x": 10, "y": 20 } } }""").RootElement)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        var response = await setup.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!.Version;
    }

    private static Task<HttpResponseMessage> SaveAsTemplateAsync(Setup setup, Guid chatbotId, string name, string category = "Support") =>
        setup.Client.PostAsJsonAsync(
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}/save-as-template",
            new SaveAsTemplateRequest(name, category, "Lo que usa la línea de servicio."),
            Ct);

    private static async Task<TemplateCatalogDto> CatalogAsync(HttpClient client, Guid tenantId) =>
        (await client.GetFromJsonAsync<TemplateCatalogDto>(TemplatesUrl(tenantId), Ct))!;

    private static bool SameJson(JsonElement actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(expected));

    // Dos bloques y sin conexiones: el flujo vuelve igual al instalar.
    private const string SimpleFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Bienvenida", "text": "Hola {nombre}", "urlButton": null, "outputs": { "next": "n2" } },
            { "id": "n2", "kind": "resolve", "title": "Listo", "text": "Gracias.", "reasonId": null } ] }
        """;

    [Fact]
    public async Task TheCatalogListsSystemAndTenantTemplates()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateBlankAsync(setup);
        await SaveDraftAsync(setup, editor, SimpleFlow);
        Assert.Equal(HttpStatusCode.Created, (await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "Línea de servicio")).StatusCode);
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var catalog = await CatalogAsync(reader, setup.Tenant.TenantId);

        Assert.Equal(["outside-hours", "welcome-menu-handoff"], catalog.System.Select(template => template.Id).Order(StringComparer.Ordinal));
        var welcome = catalog.System.Single(template => template.Id == "welcome-menu-handoff");
        Assert.Equal(("Menú de bienvenida con traspaso", "Support", false, 4), (welcome.Name, welcome.Category, welcome.OwnedByTenant, welcome.BlockCount));
        Assert.Equal(["WhatsApp"], welcome.Channels);
        Assert.Equal(["greeting", "option1", "option2"], welcome.Parameters.Select(parameter => parameter.Key));
        Assert.Equal("usa el horario de la conexión", catalog.System.Single(template => template.Id == "outside-hours").Requirement);
        var mine = Assert.Single(catalog.Mine);
        Assert.Equal(("Línea de servicio", "Support", true, 2, (string?)null), (mine.Name, mine.Category, mine.OwnedByTenant, mine.BlockCount, mine.Requirement));
        Assert.Equal(["Bienvenida", "Listo"], mine.Preview);
        Assert.Empty(mine.Parameters);
        Assert.True(Guid.TryParse(mine.Id, out var parsed) && parsed != Guid.Empty);
    }

    [Fact]
    public async Task CreateFromASystemTemplateAppliesParameters()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var source = new ChatbotSourceRequest(
            "template", "welcome-menu-handoff", null, new Dictionary<string, string> { ["greeting"] = "Hola, soy Clasic" });

        var editor = await CreatedAsync(await setup.Client.PostAsJsonAsync(
            ChatbotsUrl(setup.Tenant.TenantId), Request("Bienvenida", source, setup.WhatsAppId), Ct));

        var flow = editor.Draft.Flow.GetRawText();
        Assert.Contains("Hola, soy Clasic", flow, StringComparison.Ordinal);
        // Sin valor, el parámetro toma su valor por defecto.
        Assert.Contains("\"label\":\"Ventas\"", flow, StringComparison.Ordinal);
        Assert.DoesNotContain("{param.", flow, StringComparison.Ordinal);
        Assert.Equal("Draft", editor.Chatbot.Status);
        Assert.Equal(setup.WhatsAppId, Assert.Single(editor.Chatbot.Connections).Id);
        Assert.DoesNotContain(editor.Issues, issue => issue.Severity == nameof(FlowIssueSeverity.Error));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbot_connections WHERE active"));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.created'", ("id", editor.Chatbot.Id.ToString())));
    }

    [Fact]
    public async Task CreateFromAnUnknownTemplateIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;

        foreach (var templateId in new[] { "no-existe", Guid.CreateVersion7().ToString() })
        {
            var response = await setup.Client.PostAsJsonAsync(
                ChatbotsUrl(setup.Tenant.TenantId), Request("Bienvenida", new ChatbotSourceRequest("template", templateId, null, null)), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("automations.template.not_found", (await ProblemAsync(response)).Code);
        }

        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots"));
    }

    // D-C14: la plantilla no se lleva las conexiones «API externa»; el resto del flujo vuelve igual.
    [Fact]
    public async Task SaveAsTemplateAndCreateFromIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateBlankAsync(setup);
        await SaveDraftAsync(setup, editor, AnnexA.Json);
        var draft = JsonNode.Parse(AnnexAWithoutConnections())!;

        var save = await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "Servicio técnico Clasic", "TechnicalService");

        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var template = (await save.Content.ReadFromJsonAsync<TemplateDto>(Ct))!;
        Assert.Equal(("Servicio técnico Clasic", "TechnicalService", true, 13), (template.Name, template.Category, template.OwnedByTenant, template.BlockCount));
        Assert.Equal("Bienvenida", template.Preview[0]);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.template.created'", ("id", template.Id)));

        var created = await CreatedAsync(await setup.Client.PostAsJsonAsync(
            ChatbotsUrl(setup.Tenant.TenantId), Request("Desde mi plantilla", new ChatbotSourceRequest("template", template.Id, null, null)), Ct));

        Assert.True(JsonNode.DeepEquals(draft, JsonNode.Parse(created.Draft.Flow.GetRawText())));
        Assert.True(SameJson(created.Draft.Layout, """{ "positions": { "n1": { "x": 10, "y": 20 } } }"""));
        Assert.Empty(created.Chatbot.Connections);
        Assert.Contains(created.Issues, issue => issue.Code == FlowIssueCodes.HttpConnectionMissing);
    }

    [Fact]
    public async Task SaveAsTemplateWithARepeatedNameIs409AndAnUnknownCategoryIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateBlankAsync(setup);
        Assert.Equal(HttpStatusCode.Created, (await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "Línea de servicio")).StatusCode);

        var repeated = await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "LÍNEA DE SERVICIO");
        var category = await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "Otra", "Marketing");
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        var forbidden = await reader.PostAsJsonAsync(
            $"{ChatbotsUrl(setup.Tenant.TenantId)}/{editor.Chatbot.Id}/save-as-template", new SaveAsTemplateRequest("Otra", "Support", ""), Ct);

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("automations.template.name_taken", (await ProblemAsync(repeated)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, category.StatusCode);
        Assert.Equal(["Category"], (await ProblemAsync(category)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbot_templates"));
    }

    [Fact]
    public async Task DuplicateAsSourceCopiesTheDraft()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var original = await CreateBlankAsync(setup);
        await SaveDraftAsync(setup, original, SimpleFlow);

        var copy = await CreatedAsync(await setup.Client.PostAsJsonAsync(
            ChatbotsUrl(setup.Tenant.TenantId),
            Request("Mi copia", new ChatbotSourceRequest("duplicate", null, original.Chatbot.Id, null), setup.WhatsAppId),
            Ct));
        var missing = await setup.Client.PostAsJsonAsync(
            ChatbotsUrl(setup.Tenant.TenantId), Request("Otra copia", new ChatbotSourceRequest("duplicate", null, Guid.CreateVersion7(), null)), Ct);

        Assert.NotEqual(original.Chatbot.Id, copy.Chatbot.Id);
        Assert.Equal(("Mi copia", "Draft"), (copy.Chatbot.Name, copy.Chatbot.Status));
        Assert.True(SameJson(copy.Draft.Flow, FlowJson.Serialize(FlowJson.Parse(SimpleFlow))));
        Assert.True(SameJson(copy.Draft.Layout, """{ "positions": { "n1": { "x": 10, "y": 20 } } }"""));
        Assert.Equal(setup.WhatsAppId, Assert.Single(copy.Chatbot.Connections).Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("automations.chatbot.not_found", (await ProblemAsync(missing)).Code);
    }

    [Fact]
    public async Task DeleteASystemTemplateIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;

        var system = await setup.Client.DeleteAsync($"{TemplatesUrl(setup.Tenant.TenantId)}/welcome-menu-handoff", Ct);
        var unknown = await setup.Client.DeleteAsync($"{TemplatesUrl(setup.Tenant.TenantId)}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, system.StatusCode);
        Assert.Equal("automations.template.not_found", (await ProblemAsync(system)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("automations.template.not_found", (await ProblemAsync(unknown)).Code);
    }

    // Doble aislamiento: la plantilla de otro tenant no existe para éste.
    [Fact]
    public async Task DeleteATenantTemplateRemovesItOnlyFromItsTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        var other = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var __ = other.Client;
        var editor = await CreateBlankAsync(setup);
        var template = (await (await SaveAsTemplateAsync(setup, editor.Chatbot.Id, "Línea de servicio")).Content.ReadFromJsonAsync<TemplateDto>(Ct))!;

        var foreign = await other.Client.DeleteAsync($"{TemplatesUrl(other.Tenant.TenantId)}/{template.Id}", Ct);
        var deleted = await setup.Client.DeleteAsync($"{TemplatesUrl(setup.Tenant.TenantId)}/{template.Id}", Ct);
        var again = await setup.Client.DeleteAsync($"{TemplatesUrl(setup.Tenant.TenantId)}/{template.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty((await CatalogAsync(setup.Client, setup.Tenant.TenantId)).Mine);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.template.deleted'", ("id", template.Id)));
    }

    /// <summary>El Anexo A como lo guarda el modelo, con los <c>connectionId</c> de sus bloques HTTP en <c>null</c>.</summary>
    private static string AnnexAWithoutConnections()
    {
        var node = JsonNode.Parse(FlowJson.Serialize(FlowJson.Parse(AnnexA.Json)))!;
        foreach (var block in node["nodes"]!.AsArray())
        {
            if ((string?)block!["kind"] == "http")
            {
                block["connectionId"] = null;
            }
        }

        return node.ToJsonString();
    }
}
