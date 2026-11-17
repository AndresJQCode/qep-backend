using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (agente IA en chatbots) §6.1, §6.3 y D-B14, de punta a punta en Automations y Mensajería: el bloque delega la conversación
/// y el retorno del agente la mueve por su salida. Los workers de Ai están apagados (<c>Ai:Workers:Enabled=false</c>): lo que el agente
/// conversa lo prueba <c>Modules.Ai.IntegrationTests</c>; acá el agente «termina» por su puerto, <see cref="IAiConversations.ReturnToChatbotAsync"/>,
/// que es lo único que Ai le hace a la conversación al devolver.
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private const string AgentBlockFlow = """
        { "schema": 1, "start": { "firstNodeId": "ai" }, "variables": [],
          "nodes": [
            { "id": "ai", "kind": "aiAgent", "title": "Asesor IA", "agentId": "{agentId}",
              "objective": "Resuelve dudas de productos.", "context": "El cliente se llama {nombre_contacto}.", "firstTurn": "lastMessage",
              "idleMinutes": 60, "saveTo": "asesor",
              "outputs": { "done": "bye", "human": "desk", "noAnswer": "desk", "idle": "bye", "error": "desk" } },
            { "id": "bye", "kind": "message", "title": "Despedida", "text": "Listo: {asesor.summary}", "outputs": { "next": null } },
            { "id": "desk", "kind": "handoff", "title": "Asesor", "text": "Te paso con un asesor." }
          ]
        }
        """;

    private static readonly IReadOnlyDictionary<string, string> AiWorkersOff = new Dictionary<string, string> { ["Ai:Workers:Enabled"] = "false" };

    /// <summary>Como <c>ArrangeAsync</c>, con el módulo <c>ai</c> prendido y un agente publicado antes de publicar el chatbot (el validador
    /// exige el agente <c>Active</c>, §5.2).</summary>
    private static async Task<(Fixture Fixture, Guid AgentId)> ArrangeAgentBlockAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var notifier = new RecordingNotifier();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMessagingNotifier>();
                services.AddSingleton<IMessagingNotifier>(notifier);
            });
            foreach (var (key, value) in AiWorkersOff)
            {
                builder.UseSetting(key, value);
            }
        });
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source) VALUES (@tenantId, 'ai', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenant.TenantId));
        var agentId = await SeedPublishedAgentAsync(host, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio", PhoneNumberId, "222");
        using var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var chatbotId = await PublishChatbotAsync(
            admin, tenant.TenantId, AgentBlockFlow.Replace("{agentId}", agentId.ToString(), StringComparison.Ordinal), connectionId);
        return (new Fixture(factory, host, connectionString, tenant, connectionId, chatbotId, host.CreateClient(), notifier, new BotMeta(factory.MetaHandler)), agentId);
    }

    private static async Task<Guid> SeedPublishedAgentAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, RegisteredTenant tenant)
    {
        using var scope = host.Services.CreateScope();
        var now = DateTimeOffset.UtcNow;
        var agent = Agent.Create(
            tenant.TenantId, "Ventas", AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with { Instructions = "Atiende." },
            tenant.OwnerUserId, now);
        agent.Publish([], tenant.OwnerUserId, now);
        scope.ServiceProvider.GetRequiredService<IAgentRepository>().Add(agent);
        await scope.ServiceProvider.GetRequiredService<IAiUnitOfWork>().SaveChangesAsync(Ct);
        return agent.Id;
    }

    /// <summary>Lo que hace Ai al terminar la sesión del bloque: devolver con el resultado (§6.3).</summary>
    private static async Task ReturnFromAgentAsync(Fixture f, string resultJson)
    {
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var delegationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.bot_delegations WHERE status = 'Live'");
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiConversations>()
            .ReturnToChatbotAsync(f.Tenant.TenantId, conversationId, delegationId, resultJson, Ct);
    }

    [Fact]
    public async Task AnInboundReachesTheBlockTheAgentFinishesAndTheNextBlockSends()
    {
        await using var database = await StartDatabaseAsync();
        var (f, agentId) = await ArrangeAgentBlockAsync(database);
        using var fixture = f;

        await InboundAsync(f, "w1", "¿Tienen la camiseta en talla M?");
        await DrainAutomationsAsync(f.Host);

        // §6.1: delegación viva con el entrante y el contexto interpolado, conversación con el agente y la corrida esperando.
        Assert.Empty(f.Meta.Sends());
        Assert.Equal($"Live|{agentId}|ai", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || agent_id || '|' || node_id FROM messaging.bot_delegations"));
        Assert.Equal("El cliente se llama Laura.", await ScalarAsync<string>(f.ConnectionString,
            "SELECT brief->>'context' FROM messaging.bot_delegations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.bot_delegations d JOIN messaging.messages m ON m.id = d.last_inbound_message_id"));
        Assert.Equal("Active|2", await ConversationAsync(f, "automation_status || '|' || automation_bot_kind"));
        Assert.Equal("Waiting|ai|delegated", await RunAsync(f, "status || '|' || current_node_id || '|' || (trace->-1->>'outcome')"));

        await ReturnFromAgentAsync(f, """{"output":"done","reason":"objective_completed","summary":"Sí hay talla M"}""");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["Listo: Sí hay talla M"], f.Meta.Sends());
        Assert.Equal("Completed|Sí hay talla M", await RunAsync(f, "status || '|' || (variables->'asesor'->>'summary')"));
        Assert.Equal("None", await ConversationAsync(f, "automation_status"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.automation_inbox WHERE kind = 'AgentReturned' AND outcome = 'ended'"));
    }

    // D-B14: la toma humana avisa a los dos bots (B1.3); la corrida termina Interrupted sin salir por ninguna salida.
    [Fact]
    public async Task TakingOverDuringTheDelegationEndsSessionAndRun()
    {
        await using var database = await StartDatabaseAsync();
        var (f, _) = await ArrangeAgentBlockAsync(database);
        using var fixture = f;
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "take")).StatusCode);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Interrupted", await RunAsync(f, "status"));
        Assert.Equal("Interrupted", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.bot_delegations"));
        Assert.Equal(2L, await ReleaseRowsAsync(f));
        Assert.Empty(f.Meta.Sends());
    }
}
