using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Model.Anthropic;
using Modules.Ai.Infrastructure.Model.OpenAi;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-10-29 §7 y plan 2026-11-12, 5.5: el playground corre el mismo bucle que una conversación real, con el proveedor <b>del
/// borrador</b>, contra una Messages API falsa. La respuesta va a la transcripción; las herramientas y el consumo quedan en <c>ai.*</c>.
/// </summary>
public sealed class AgentPlaygroundApiTests
{
    private static readonly string[] Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage];

    private static string Agents => $"{AiUrl()}/agents";

    private static string Tests => $"{AiUrl()}/tests";

    [Fact]
    public async Task OpeningATestWithoutTheAccountOfTheDraftProviderIs422NamingTheProvider()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        var agentId = await DraftAgentAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, $"{Agents}/{agentId}/tests");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("ai.provider.not_configured", problem.GetProperty("code").GetString());
        Assert.Equal("anthropic", problem.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task AMessageRunsTheDraftAgentWithItsToolsAndLeavesTheReplyToolCallsAndUsage()
    {
        await using var database = await StartDatabaseAsync();
        using var gate = new SemaphoreSlim(0);
        var anthropic = new FakeAnthropicMessagesHandler(AnthropicTestKey) { Gate = gate };
        anthropic.Enqueue(FakeAnthropicMessagesHandler.ToolUse("toolu_01", "search_catalog", """{"query":"jabón de avena","limit":3}"""));
        anthropic.Enqueue(FakeAnthropicMessagesHandler.EndTurn("No encontré ese jabón, ¿te ayudo con otro?"));
        using var factory = new QepApiFactory(database.GetConnectionString(), services => UseFakeAnthropic(services, anthropic));
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        var agentId = await DraftAgentAsync(client);

        var opened = await SendAsync(client, HttpMethod.Post, $"{Agents}/{agentId}/tests", new { customerId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var testId = (await JsonAsync(opened)).GetProperty("id").GetGuid();

        var sent = await SendAsync(client, HttpMethod.Post, $"{Tests}/{testId}/messages", new { text = "¿Tienen jabón de avena?" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.True((await JsonAsync(sent)).GetProperty("pending").GetBoolean());

        // Mientras el modelo «piensa», otro mensaje es 409.
        var busy = await SendAsync(client, HttpMethod.Post, $"{Tests}/{testId}/messages", new { text = "¿Hola?" });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("ai.test.busy", (await JsonAsync(busy)).GetProperty("code").GetString());

        gate.Release(2);
        var test = await WaitUntilAnsweredAsync(client, testId);

        var transcript = test.GetProperty("transcript").EnumerateArray().ToArray();
        Assert.Equal(["customer", "agent"], transcript.Select(entry => entry.GetProperty("role").GetString()));
        Assert.Equal("No encontré ese jabón, ¿te ayudo con otro?", transcript[1].GetProperty("text").GetString());
        Assert.False(test.GetProperty("handedOff").GetBoolean());

        var call = Assert.Single(test.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(("toolu_01", "search_catalog"), (call.GetProperty("callId").GetString(), call.GetProperty("tool").GetString()));
        Assert.Equal("jabón de avena", call.GetProperty("arguments").GetProperty("query").GetString());

        var usage = Assert.Single(test.GetProperty("usage").EnumerateArray());
        Assert.Equal("anthropic", usage.GetProperty("provider").GetString());
        Assert.Equal(2, usage.GetProperty("calls").GetInt32());
        Assert.Equal(3100, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(1400, usage.GetProperty("cacheReadInputTokens").GetInt64());

        // C-4: una fila por llamada, con el proveedor, el modelo pedido y el servido, sin versión (es el borrador).
        Assert.Equal(2L, await ScalarAsync<long>(
            database.GetConnectionString(),
            "SELECT count(*) FROM ai.usage_records WHERE test_id = @test AND provider = 'anthropic' AND model = 'claude-sonnet-5-5' AND agent_version_id IS NULL",
            ("test", testId)));
        Assert.Equal("claude-opus-5", await ScalarAsync<string>(
            database.GetConnectionString(),
            "SELECT served_model FROM ai.usage_records WHERE test_id = @test ORDER BY id DESC LIMIT 1",
            ("test", testId)));
        Assert.Equal("Done", await ScalarAsync<string>(
            database.GetConnectionString(), "SELECT status FROM ai.model_jobs WHERE test_id = @test", ("test", testId)));
        // C-14: cada llamada salió con la clave de la cuenta del tenant.
        Assert.Equal(2, anthropic.Requests.Count);
        Assert.All(anthropic.Requests, request => Assert.True(request.KeyMatched, "The call must carry the tenant's own key."));
    }

    // Plan 2026-11-17 A4 (F-4): una respuesta que cita una FAQ trae su fuente resuelta a la pregunta, como la actividad real; los ids crudos
    // siguen viajando.
    [Fact]
    public async Task AReplyBasedOnAFaqShowsTheQuestion()
    {
        await using var database = await StartDatabaseAsync();
        var anthropic = new FakeAnthropicMessagesHandler(AnthropicTestKey);
        anthropic.Enqueue(FakeAnthropicMessagesHandler.ToolUse(
            "toolu_01", AgentToolNames.SendReply, """{"text":"Sí, enviamos a todo el país.","sourceIds":["envios"]}"""));
        using var factory = new QepApiFactory(database.GetConnectionString(), services => UseFakeAnthropic(services, anthropic));
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        var agentId = await DraftAgentAsync(client);

        var opened = await SendAsync(client, HttpMethod.Post, $"{Agents}/{agentId}/tests", new { customerId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var testId = (await JsonAsync(opened)).GetProperty("id").GetGuid();
        var sent = await SendAsync(client, HttpMethod.Post, $"{Tests}/{testId}/messages", new { text = "¿Hacen envíos a Cali?" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var test = await WaitUntilAnsweredAsync(client, testId);

        var reply = Assert.Single(test.GetProperty("transcript").EnumerateArray(), entry => entry.GetProperty("role").GetString() == "agent");
        Assert.Equal(["envios"], reply.GetProperty("sourceIds").EnumerateArray().Select(id => id.GetString()));
        var source = Assert.Single(reply.GetProperty("sources").EnumerateArray());
        Assert.Equal(
            ("faq", "envios", "¿Hacen envíos?"),
            (source.GetProperty("kind").GetString(), source.GetProperty("id").GetString(), source.GetProperty("label").GetString()));
    }

    // Spec 2026-11-17 §7: la prueba en modo bloque ofrece finish_objective, lleva el objetivo en el contexto volátil y muestra el retorno.
    [Fact]
    public async Task ABlockTestOffersFinishObjectiveAndShowsTheReturn()
    {
        await using var database = await StartDatabaseAsync();
        var anthropic = new FakeAnthropicMessagesHandler(AnthropicTestKey);
        anthropic.Enqueue(FakeAnthropicMessagesHandler.ToolUse("toolu_01", AgentToolNames.FinishObjective, """{"summary":"Quiere 3 jabones de avena."}"""));
        using var factory = new QepApiFactory(database.GetConnectionString(), services => UseFakeAnthropic(services, anthropic));
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        var agentId = await DraftAgentAsync(client);

        var opened = await SendAsync(
            client, HttpMethod.Post, $"{Agents}/{agentId}/tests",
            new { customerId = (Guid?)null, block = new { objective = "Toma el pedido de jabones.", context = "Eligió «Comprar»." } });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var openedBody = await JsonAsync(opened);
        Assert.Equal("Toma el pedido de jabones.", openedBody.GetProperty("block").GetProperty("objective").GetString());
        var testId = openedBody.GetProperty("id").GetGuid();
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await SendAsync(client, HttpMethod.Post, $"{Tests}/{testId}/messages", new { text = "Quiero 3 de avena." })).StatusCode);
        var test = await WaitUntilAnsweredAsync(client, testId);

        var returned = test.GetProperty("returned");
        Assert.Equal(
            ("done", "objective_completed", "Quiere 3 jabones de avena."),
            (returned.GetProperty("output").GetString(), returned.GetProperty("reason").GetString(), returned.GetProperty("summary").GetString()));
        Assert.Equal(["customer", "returned"], test.GetProperty("transcript").EnumerateArray().Select(entry => entry.GetProperty("role").GetString()));
        Assert.False(test.GetProperty("handedOff").GetBoolean());
        // Una sola llamada: finish_objective es final y no pide otra vuelta del modelo.
        Assert.True(Assert.Single(anthropic.Requests).KeyMatched);
    }

    [Fact]
    public async Task ABlockTestWithoutObjectiveIs422WithTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        await SeedAnthropicConnectionAsync(factory);
        using var client = CreateClient(factory, permissions: Manage);
        var agentId = await DraftAgentAsync(client);

        var response = await SendAsync(client, HttpMethod.Post, $"{Agents}/{agentId}/tests", new { block = new { objective = " " } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Block.Objective", out _));
    }

    internal static void UseFakeAnthropic(IServiceCollection services, HttpMessageHandler handler) =>
        services.AddHttpClient(ClaudeModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

    internal static void UseFakeOpenAi(IServiceCollection services, HttpMessageHandler handler) =>
        services.AddHttpClient(OpenAiModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

    internal static async Task<Guid> DraftAgentAsync(HttpClient client, string provider = "anthropic", string model = "claude-sonnet-5-5")
    {
        var created = await SendAsync(client, HttpMethod.Post, Agents, new { name = "Asesor virtual", source = new { kind = "blank" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agentId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var draft = await SendAsync(
            client,
            HttpMethod.Put,
            $"{Agents}/{agentId}/draft",
            new
            {
                instructions = "Atiende las dudas de los clientes de la tienda de jabones.",
                tone = "friendly",
                greeting = (string?)null,
                faq = new[] { new { id = "envios", question = "¿Hacen envíos?", answer = "Sí, a todo el país." } },
                useQuickReplies = false,
                answerOnlyFromSources = true,
                tools = new { catalog = true, orderStatus = false, quotationDraft = false },
                handoff = new { destination = "unassigned", memberId = (Guid?)null, message = (string?)null, maxRepliesWithoutHuman = 20, maxConsecutiveFailures = 2 },
                trigger = "NewConversation",
                model = new { provider, id = model },
            },
            ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        return agentId;
    }

    /// <summary>El worker corre en el host de la prueba (tick de 1 s): se sondea como lo hace el frontend.</summary>
    internal static async Task<JsonElement> WaitUntilAnsweredAsync(HttpClient client, Guid testId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await client.GetAsync($"{Tests}/{testId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var test = await JsonAsync(response);
            if (!test.GetProperty("pending").GetBoolean())
            {
                return test;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The test job did not finish in 30 s.");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
