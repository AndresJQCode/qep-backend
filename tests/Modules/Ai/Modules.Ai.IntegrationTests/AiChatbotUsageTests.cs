using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §7 y D-B18: el adaptador de Bootstrapper lee los bloques «Agente IA» de la versión publicada de cada chatbot con SQL sobre
/// <c>automations</c>. Sólo cuenta lo publicado y del tenant, y un <c>agentId</c> mal formado en un flujo no tumba la lectura.
/// </summary>
public sealed class AiChatbotUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyThePublishedVersionOfTheTenantsChatbotsCounts()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, settings: new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" });
        await SeedTenantAsync(factory);
        var tenant = Guid.Parse(TenantId);
        var agent = Guid.CreateVersion7();
        var published = await SeedChatbotAsync(connectionString, tenant, "Bienvenida", Flow(agent.ToString()), publish: true);
        await SeedChatbotAsync(connectionString, tenant, "Roto", Flow("no-es-un-id"), publish: true);
        await SeedChatbotAsync(connectionString, tenant, "Borrador", Flow(agent.ToString()), publish: false);

        await using var scope = factory.Services.CreateAsyncScope();
        var usage = await scope.ServiceProvider.GetRequiredService<IAiChatbotUsage>().ListByAgentAsync(tenant, Ct);
        var foreign = await scope.ServiceProvider.GetRequiredService<IAiChatbotUsage>().ListByAgentAsync(Guid.CreateVersion7(), Ct);

        var entry = Assert.Single(usage);
        Assert.Equal(agent, entry.Key);
        Assert.Equal([new AiChatbotRef(published, "Bienvenida")], entry.Value);
        Assert.Empty(foreign);
    }

    private static string Flow(string agentId) =>
        $$$"""
        {"schema":1,"start":{"firstNodeId":"ai-1"},"variables":[],"nodes":[{"id":"ai-1","kind":"aiAgent","title":"Asesor IA","outputs":{},"agentId":"{{{agentId}}}","objective":"Resuelve dudas.","context":"","firstTurn":"lastMessage","idleMinutes":60,"saveTo":"asesor_ia"},{"id":"m-1","kind":"message","title":"Hola","outputs":{}}]}
        """;

    private static async Task<Guid> SeedChatbotAsync(string connectionString, Guid tenant, string name, string flow, bool publish)
    {
        var (chatbot, version) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@chatbot, @tenant, @name, @flow::jsonb, now(), @tenant, now(), @tenant, now());
            """,
            ("chatbot", chatbot), ("tenant", tenant), ("name", name), ("flow", flow));
        if (publish)
        {
            await ExecuteAsync(
                connectionString,
                """
                INSERT INTO automations.chatbot_versions (id, chatbot_id, tenant_id, number, flow, layout, published_at, published_by)
                VALUES (@version, @chatbot, @tenant, 1, @flow::jsonb, '{}'::jsonb, now(), @tenant);
                UPDATE automations.chatbots SET status = 'Active', published_version_id = @version WHERE id = @chatbot;
                """,
                ("chatbot", chatbot), ("version", version), ("tenant", tenant), ("flow", flow));
        }

        return chatbot;
    }
}
