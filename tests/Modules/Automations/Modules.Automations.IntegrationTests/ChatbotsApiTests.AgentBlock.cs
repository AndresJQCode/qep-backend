using System.Net;
using System.Net.Http.Json;
using Modules.Automations.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.2 y §7: el editor trae los agentes del tenant y si tiene el módulo <c>ai</c>; publicar
/// rechaza un bloque con un agente en borrador y avisa del pausado; la lista suma el consumo de IA del mes por chatbot.</summary>
public sealed partial class ChatbotsApiTests
{
    private static string AgentFlow(Guid agentId) => $$"""
        { "schema": 1, "start": { "firstNodeId": "ai" }, "variables": [],
          "nodes": [
            { "id": "ai", "kind": "aiAgent", "title": "Asesor IA", "agentId": "{{agentId}}", "objective": "Resuelve dudas.", "context": "",
              "saveTo": "asesor", "outputs": { "done": "end", "human": "end", "noAnswer": "end", "idle": "end", "error": "end" } },
            { "id": "end", "kind": "handoff", "title": "Asesor", "text": null }
          ] }
        """;

    [Fact]
    public async Task TheEditorOffersAgentsAndPublishingRejectsAPausedOrDraftAgent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;

        // Sin el módulo ai: el editor no ofrece agentes y el bloque no se puede publicar (D-B16).
        var draftAgent = await SeedAgentAsync(factory, setup.Tenant, "Nuevo", publish: false);
        var withoutAi = await CreateAsync(setup, "Sin IA");
        Assert.False(withoutAi.AiEnabled);
        Assert.Empty(withoutAi.Agents);

        await EnableAiAsync(connectionString, setup.Tenant.TenantId);
        var pausedAgent = await SeedAgentAsync(factory, setup.Tenant, "Viejo", pause: true);
        var editor = await CreateAsync(setup);

        Assert.True(editor.AiEnabled);
        Assert.Equal(
            [("Nuevo", "Draft"), ("Viejo", "Paused")],
            editor.Agents.Select(agent => (agent.Name, agent.Status)).OrderBy(agent => agent.Name, StringComparer.Ordinal));
        Assert.All(editor.Agents, agent => Assert.Equal(("anthropic", "claude-sonnet-5-5"), (agent.Provider, agent.Model)));

        // Un agente en borrador: 422 con ai_agent.agent_not_published.
        var withDraft = await SaveDraftAsync(setup, editor, AgentFlow(draftAgent));
        Assert.Equal(HttpStatusCode.OK, withDraft.StatusCode);
        var saved = (await withDraft.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.Contains(saved.Issues, issue => issue.Code == "ai_agent.agent_not_published" && issue.Severity == "Error");
        var rejected = await PublishAsync(setup.Client, setup, editor.Chatbot.Id, saved.Version, []);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        // Un agente pausado: sólo aviso, se publica.
        var current = await GetAsync(setup, editor.Chatbot.Id);
        var withPaused = await SaveDraftAsync(setup, current, AgentFlow(pausedAgent));
        var pausedSaved = (await withPaused.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!;
        Assert.Contains(pausedSaved.Issues, issue => issue.Code == "ai_agent.agent_paused" && issue.Severity == "Warning");
        var published = await PublishAsync(setup.Client, setup, editor.Chatbot.Id, pausedSaved.Version, []);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    [Fact]
    public async Task TheListSumsTheAiUsageOfTheMonthPerChatbot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        await EnableAiAsync(connectionString, setup.Tenant.TenantId);
        var agentId = await SeedAgentAsync(factory, setup.Tenant);
        var used = await CreateAsync(setup, "Con IA");
        var unused = await CreateAsync(setup, "Sin consumo");
        // Dos llamadas al modelo de una sesión de bloque de «Con IA» este mes y una del mes pasado, que no cuenta.
        var sessionId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO ai.sessions (id, tenant_id, conversation_id, connection_id, agent_id, agent_version_id, status, started_at, updated_at,
                                     origin, delegation_id, chatbot_id, run_id)
            SELECT @id, @tenantId, gen_random_uuid(), gen_random_uuid(), a.id, a.published_version_id, 'Returned', now(), now(),
                   'ChatbotBlock', gen_random_uuid(), @chatbotId, gen_random_uuid()
              FROM ai.agents a WHERE a.id = @agentId
            """,
            ("id", sessionId), ("tenantId", setup.Tenant.TenantId), ("chatbotId", used.Chatbot.Id), ("agentId", agentId));
        await ExecuteAsync(connectionString,
            """
            INSERT INTO ai.usage_records (tenant_id, agent_id, session_id, job_id, provider, model, input_tokens, output_tokens, outcome, called_at)
            VALUES (@tenantId, @agentId, @sessionId, gen_random_uuid(), 'anthropic', 'claude-sonnet-5-5', 1000, 300, 'ok', now()),
                   (@tenantId, @agentId, @sessionId, gen_random_uuid(), 'anthropic', 'claude-sonnet-5-5', 200, 40, 'ok', now()),
                   (@tenantId, @agentId, @sessionId, gen_random_uuid(), 'anthropic', 'claude-sonnet-5-5', 9999, 9999, 'ok', now() - interval '45 days')
            """,
            ("tenantId", setup.Tenant.TenantId), ("agentId", agentId), ("sessionId", sessionId));

        var list = (await setup.Client.GetFromJsonAsync<ChatbotListResponse>(ChatbotsUrl(setup.Tenant.TenantId), Ct))!;

        Assert.Equal(new ChatbotAiUsageDto(1200, 340), list.Items.Single(item => item.Id == used.Chatbot.Id).AiUsageThisMonth);
        Assert.Null(list.Items.Single(item => item.Id == unused.Chatbot.Id).AiUsageThisMonth);
    }
}
