using System.Net;
using System.Text.Json;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §4: la galería de plantillas de agente del sistema y crear un agente desde una. <c>available</c> lo calcula el backend
/// con los módulos efectivos del tenant; el frontend sólo lo pinta.
/// </summary>
public sealed class AgentTemplatesApiTests
{
    private static readonly string[] Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Templates => $"{AiUrl()}/agent-templates";

    private static Task<int> DisableSchedulingAsync(string connectionString) =>
        ExecuteAsync(
            connectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'scheduling'",
            ("t", Guid.Parse(TenantId)));

    [Fact]
    public async Task TheTemplateCatalogSaysWhatTheTenantCanAdopt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);

        var before = await JsonAsync(await client.GetAsync(Templates, Ct));
        await DisableSchedulingAsync(database.GetConnectionString());
        var after = await JsonAsync(await client.GetAsync(Templates, Ct));

        var template = Assert.Single(before.EnumerateArray());
        Assert.Equal("asistente-de-citas", template.GetProperty("id").GetString());
        Assert.Equal("Asistente de citas", template.GetProperty("name").GetString());
        Assert.Equal(["scheduling"], template.GetProperty("requires").EnumerateArray().Select(key => key.GetString()));
        Assert.True(template.GetProperty("available").GetBoolean());
        Assert.False(Assert.Single(after.EnumerateArray()).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ListingTemplatesNeedsManage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var reader = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var response = await reader.GetAsync(Templates, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string FromTemplate => $"{AiUrl()}/agents/from-template";

    private static async Task<(string? Code, JsonElement Root)> ProblemAsync(HttpResponseMessage response)
    {
        var root = await JsonAsync(response);
        return (root.TryGetProperty("code", out var code) ? code.GetString() : null, root);
    }

    [Fact]
    public async Task AnAgentFromATemplateIsBornAsADraftWithTheTemplatesConfig()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);

        var response = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "  Citas  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var agent = await JsonAsync(response);
        Assert.Equal("Citas", agent.GetProperty("name").GetString());
        Assert.Equal("Draft", agent.GetProperty("status").GetString());
        Assert.Equal(1, agent.GetProperty("version").GetInt64());
        Assert.Empty(agent.GetProperty("connectionIds").EnumerateArray());
        var config = agent.GetProperty("draft").GetProperty("config");
        var model = config.GetProperty("model");
        Assert.Equal(("anthropic", "claude-opus-5-5"), (model.GetProperty("provider").GetString(), model.GetProperty("id").GetString()));
        Assert.True(config.GetProperty("tools").GetProperty("scheduling").GetBoolean());
        Assert.Equal("friendly", config.GetProperty("tone").GetString());
        Assert.Equal(2, config.GetProperty("faq").GetArrayLength());
        var issues = agent.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("code").GetString()).ToList();
        Assert.Contains("connections_missing", issues);
        Assert.DoesNotContain("instructions_missing", issues);
        Assert.Equal(1, await ScalarAsync<long>(
            database.GetConnectionString(),
            "SELECT count(*) FROM audit.entries WHERE action = 'ai.agent.created' AND resource_id = @id",
            ("id", agent.GetProperty("id").GetGuid().ToString())));
    }

    // Spec 2026-11-23 menores M2: sólo crear desde una plantilla guarda el origen; duplicar e importar no lo heredan.
    [Fact]
    public async Task AnAgentRemembersTheTemplateItCameFromAndNothingElseCopiesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);

        var created = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "Citas" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agent = await JsonAsync(created);
        var id = agent.GetProperty("id").GetGuid();
        Assert.Equal(("asistente-de-citas", "Asistente de citas"), (agent.GetProperty("sourceTemplateId").GetString(), agent.GetProperty("sourceTemplateName").GetString()));
        var read = await JsonAsync(await client.GetAsync($"{AiUrl()}/agents/{id}", Ct));
        Assert.Equal(("asistente-de-citas", "Asistente de citas"), (read.GetProperty("sourceTemplateId").GetString(), read.GetProperty("sourceTemplateName").GetString()));
        Assert.Equal("asistente-de-citas", await ScalarAsync<string>(
            database.GetConnectionString(), "SELECT source_template_id FROM ai.agents WHERE id = @id", ("id", id)));

        var export = await JsonAsync(await client.GetAsync($"{AiUrl()}/agents/{id}/export", Ct));
        Assert.False(export.TryGetProperty("sourceTemplateId", out _));

        var copy = await JsonAsync(await SendAsync(
            client, HttpMethod.Post, $"{AiUrl()}/agents", new { name = "Citas copia", source = new { kind = "duplicate", agentId = id } }));
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null),
            (copy.GetProperty("sourceTemplateId").ValueKind, copy.GetProperty("sourceTemplateName").ValueKind));

        var imported = await JsonAsync(await SendAsync(client, HttpMethod.Post, $"{AiUrl()}/agents/import", new { name = "Citas importado", export }));
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null),
            (imported.GetProperty("sourceTemplateId").ValueKind, imported.GetProperty("sourceTemplateName").ValueKind));
    }

    [Fact]
    public async Task AdoptingATemplateIsRejectedWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        using var reader = CreateClient(factory, permissions: AiPermissions.AgentRead);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "Citas" })).StatusCode);

        var missing = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "no-existe", name = "Otra" });
        // Una plantilla de chatbot no se cruza con las de agente.
        var chatbot = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "welcome-menu-handoff", name = "Otra" });
        var taken = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "citas" });
        var blank = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = string.Empty });
        var denied = await SendAsync(reader, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "Otra" });
        // Una pestaña vieja con la galería de antes: la Agenda se apagó después de pintarla.
        await DisableSchedulingAsync(database.GetConnectionString());
        var off = await SendAsync(client, HttpMethod.Post, FromTemplate, new { templateId = "asistente-de-citas", name = "Otra" });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("ai.template.not_found", (await ProblemAsync(missing)).Code);
        Assert.Equal(HttpStatusCode.NotFound, chatbot.StatusCode);
        Assert.Equal("ai.template.not_found", (await ProblemAsync(chatbot)).Code);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("ai.agent.name_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal("validation.failed", (await ProblemAsync(blank)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, off.StatusCode);
        Assert.Equal("ai.template.module_not_enabled", (await ProblemAsync(off)).Code);
        var agents = await JsonAsync(await client.GetAsync($"{AiUrl()}/agents", Ct));
        Assert.Equal(1, agents.GetProperty("items").GetArrayLength());
    }
}
