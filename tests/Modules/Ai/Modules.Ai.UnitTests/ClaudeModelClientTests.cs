using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Model.Anthropic;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Plan 2026-11-12, 5.3: lo que <see cref="ClaudeModelClient"/> manda a la Messages API, contra un servidor falso que captura el cuerpo.
/// Cada prueba amarra una regla de «Llamada al modelo con Anthropic» (C-6..C-15) que el siguiente va a querer simplificar.
/// </summary>
public sealed class ClaudeModelClientTests : IDisposable
{
    private const string TenantKey = "sk-ant-api03-TEST-tenant-one";
    private const string OtherTenantKey = "sk-ant-api03-TEST-tenant-two";

    private readonly FakeAnthropicServer _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ItSendsNoThinkingAndAnExplicitEffort()
    {
        await CompleteAsync(Request());

        var body = _server.Last.Body;
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal(16_000, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("/v1/messages", _server.Last.Path);
    }

    [Fact]
    public async Task ToolsAreStrictSortedByNameAndNeverForced()
    {
        await CompleteAsync(Request() with { Tools = [Tool("send_reply"), Tool("handoff_to_human"), Tool("search_catalog")] });

        var body = _server.Last.Body;
        var tools = body.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["handoff_to_human", "search_catalog", "send_reply"], tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.All(tools, tool => Assert.True(tool.GetProperty("strict").GetBoolean()));
        Assert.All(tools, tool => Assert.False(tool.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean()));
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    // Tope de Anthropic: 20 herramientas strict por request. Una herramienta que se declara no estricta va sin strict.
    [Fact]
    public async Task ANonStrictToolGoesWithoutStrict()
    {
        await CompleteAsync(Request() with { Tools = [Tool("send_reply"), Tool("list_orders_report") with { Strict = false }] });

        var tools = _server.Last.Body.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        Assert.True(tools["send_reply"].GetProperty("strict").GetBoolean());
        Assert.False(tools["list_orders_report"].TryGetProperty("strict", out var strict) && strict.GetBoolean());
    }

    [Fact]
    public async Task AnEndTurnWithTextComesBackAsASendReplyWithoutSources()
    {
        _server.Respond = () => FakeAnthropicServer.Ok(FakeAnthropicServer.EndTurnBody("Claro, te ayudo."));

        var response = await CompleteAsync(Request());

        Assert.Equal(AiStopReason.EndTurn, response.StopReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("send_reply", call.Name);
        var arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement;
        Assert.Equal("Claro, te ayudo.", arguments.GetProperty("text").GetString());
        Assert.Equal(0, arguments.GetProperty("sourceIds").GetArrayLength());
    }

    [Fact]
    public async Task TheSystemPromptCarriesTheOnlyCachePointAndTheVolatileContextGoesAfterIt()
    {
        await CompleteAsync(Request() with { VolatileContext = "Fecha y hora: 2026-11-13 10:00" });

        var body = _server.Last.Body;
        var system = Assert.Single(body.GetProperty("system").EnumerateArray());
        Assert.Equal("Eres el asistente de la tienda.", system.GetProperty("text").GetString());
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.DoesNotContain("2026-11-13", body.GetProperty("system").GetRawText(), StringComparison.Ordinal);
        // Caché automático del historial (nivel superior).
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
        var last = body.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("system", last.GetProperty("role").GetString());
        Assert.Contains("2026-11-13 10:00", last.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGreetingIsASystemMessageAfterTheFirstCustomerTurnAndTheRequestNeverEndsInAssistant()
    {
        await CompleteAsync(Request() with
        {
            GreetingSent = "¡Hola! Soy el asistente.",
            Messages =
            [
                new AiChatMessage(AiChatRole.User, "hola"),
                new AiChatMessage(AiChatRole.Assistant, "¿En qué te ayudo?"),
                new AiChatMessage(AiChatRole.User, "precio del jabón"),
            ],
        });

        var messages = _server.Last.Body.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user", "system", "assistant", "user", "system"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Contains("¡Hola! Soy el asistente.", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.NotEqual("assistant", messages[^1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task WithOnlyOneCustomerTurnTheGreetingAndTheVolatileContextShareOneSystemMessage()
    {
        await CompleteAsync(Request() with { GreetingSent = "¡Hola!" });

        var messages = _server.Last.Body.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user", "system"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Contains("¡Hola!", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Fecha", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-haiku-5-5", false)]
    public async Task TheRefusalFallbackGoesOnOpusAndSonnetButNotOnHaiku(string model, bool expected)
    {
        await CompleteAsync(Request() with { Model = model });

        var body = _server.Last.Body;
        Assert.Equal(expected, body.TryGetProperty("fallbacks", out var fallbacks));
        Assert.Equal(expected, _server.Last.Betas?.Contains("server-side-fallback-2026-07-01", StringComparison.Ordinal) ?? false);
        if (expected)
        {
            Assert.Equal("default", fallbacks.GetString());
        }
    }

    [Fact]
    public async Task ThePriorAssistantBlocksAreResentVerbatimAndAllToolResultsGoInOneUserMessage()
    {
        const string thinking = """{"type":"thinking","thinking":"","signature":"EqQBCkYIBxgCKkB+firma/opaca=="}""";
        const string toolUseA = """{"type":"tool_use","id":"toolu_a","name":"search_catalog","input":{"query":"jabón","limit":5}}""";
        const string toolUseB = """{"type":"tool_use","id":"toolu_b","name":"get_order_status","input":{"orderNumber":null}}""";
        var round = new AiModelRound(
            [new AiOpaqueBlock(thinking), new AiOpaqueBlock(toolUseA), new AiOpaqueBlock(toolUseB)],
            [new AiToolResult("toolu_a", """[{"name":"Jabón"}]""", false), new AiToolResult("toolu_b", """{"error":"customer_unknown"}""", true)]);

        await CompleteAsync(Request() with { PriorAssistantBlocks = [round] });

        var messages = _server.Last.Body.GetProperty("messages").EnumerateArray().ToArray();
        var assistant = messages[^2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var blocks = assistant.GetProperty("content").EnumerateArray().Select(block => block.GetRawText()).ToArray();
        Assert.Equal([Canonical(thinking), Canonical(toolUseA), Canonical(toolUseB)], blocks.Select(Canonical));
        var results = messages[^1];
        Assert.Equal("user", results.GetProperty("role").GetString());
        var content = results.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(["toolu_a", "toolu_b"], content.Select(block => block.GetProperty("tool_use_id").GetString()));
        Assert.False(content[0].TryGetProperty("is_error", out var first) && first.GetBoolean());
        Assert.True(content[1].GetProperty("is_error").GetBoolean());
        // Append-only: el contexto volátil quedó donde estaba en la primera llamada, antes de la vuelta.
        Assert.Equal("system", messages[^3].GetProperty("role").GetString());
    }

    [Fact]
    public async Task AResponseBecomesToolCallsOpaqueBlocksUsageAndServedModel()
    {
        _server.Respond = () => FakeAnthropicServer.Ok("""
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5","stop_reason":"tool_use","stop_sequence":null,
             "content":[
               {"type":"thinking","thinking":"","signature":"sig-1"},
               {"type":"text","text":"Busco eso."},
               {"type":"tool_use","id":"toolu_9","name":"search_catalog","input":{"query":"jabón","limit":3}}],
             "usage":{"input_tokens":1200,"output_tokens":45,"cache_creation_input_tokens":null,"cache_read_input_tokens":1000}}
            """);

        var response = await CompleteAsync(Request());

        Assert.Equal(AiStopReason.ToolUse, response.StopReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(("toolu_9", "search_catalog"), (call.Id, call.Name));
        Assert.Equal(3, JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("Busco eso.", response.Text);
        Assert.Equal(3, response.OpaqueAssistantBlocks.Count);
        Assert.Contains("sig-1", response.OpaqueAssistantBlocks[0].Json, StringComparison.Ordinal);
        Assert.Equal(new AiModelUsage(1200, 45, null, 1000), response.Usage);
        Assert.Equal("claude-opus-5", response.ServedModel);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", typeof(AiInvalidRequestException))]
    [InlineData(413, "request_too_large", typeof(AiInvalidRequestException))]
    [InlineData(401, "authentication_error", typeof(AiCredentialsRejectedException))]
    [InlineData(402, "billing_error", typeof(AiQuotaExhaustedException))]
    [InlineData(403, "permission_error", typeof(AiPermissionDeniedException))]
    [InlineData(404, "not_found_error", typeof(AiModelUnavailableException))]
    [InlineData(429, "rate_limit_error", typeof(AiRateLimitedException))]
    [InlineData(500, "api_error", typeof(AiProviderUnavailableException))]
    [InlineData(529, "overloaded_error", typeof(AiProviderUnavailableException))]
    public async Task EveryProviderErrorMapsToItsTypedError(int status, string type, Type expected)
    {
        _server.Respond = () => FakeAnthropicServer.Error(status, type);

        var failure = await Assert.ThrowsAnyAsync<AiModelException>(() => CompleteAsync(Request()));

        Assert.IsType(expected, failure);
    }

    [Fact]
    public async Task ARateLimitCarriesItsRetryAfter()
    {
        _server.Respond = () => FakeAnthropicServer.Error(429, "rate_limit_error", retryAfter: "7");

        var failure = await Assert.ThrowsAsync<AiRateLimitedException>(() => CompleteAsync(Request()));

        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task ANetworkFailureIsProviderUnavailable()
    {
        _server.FailWithNetworkError = true;

        var failure = await Assert.ThrowsAsync<AiProviderUnavailableException>(() => CompleteAsync(Request()));

        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task ARefusalIsTypedWithItsCategoryAndKeepsTheUsage()
    {
        _server.Respond = () => FakeAnthropicServer.Ok("""
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"refusal","stop_sequence":null,
             "stop_details":{"type":"refusal","category":"cyber","explanation":"x"},
             "content":[],"usage":{"input_tokens":50,"output_tokens":0}}
            """);

        var failure = await Assert.ThrowsAsync<AiRefusedException>(() => CompleteAsync(Request()));

        Assert.Equal("cyber", failure.Category);
        Assert.Equal(50, failure.Usage!.Input);
    }

    [Fact]
    public async Task AResponseCutByMaxTokensIsAnInvalidResponseThatKeepsTheUsage()
    {
        _server.Respond = () => FakeAnthropicServer.Ok("""
            {"id":"msg_4","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"max_tokens","stop_sequence":null,
             "content":[{"type":"text","text":"Te cuen"}],"usage":{"input_tokens":50,"output_tokens":16000}}
            """);

        var failure = await Assert.ThrowsAsync<AiInvalidResponseException>(() => CompleteAsync(Request()));

        Assert.Equal(16_000, failure.Usage!.Output);
    }

    [Fact]
    public async Task EachRequestUsesTheKeyOfItsOwnCredential()
    {
        await CompleteAsync(Request());
        await CompleteAsync(Request() with { Credential = new AiCredential(Guid.NewGuid(), OtherTenantKey) });

        var keys = _server.Requests.Select(request => request.Key).ToArray();
        Assert.True(keys[0] == TenantKey, "The first request must carry the first tenant's key.");
        Assert.True(keys[1] == OtherTenantKey, "The second request must carry the second tenant's key, not a shared one.");
    }

    [Fact]
    public async Task TheSdkSpansCarryNeitherTheKeyNorTheConversation()
    {
        var tags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("com.anthropic.sdk.csharp", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (tags)
                {
                    tags.AddRange(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
                    tags.AddRange(activity.Events.SelectMany(@event => @event.Tags.Select(tag => $"{tag.Key}={tag.Value}")));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await CompleteAsync(Request() with { Messages = [new AiChatMessage(AiChatRole.User, "mi cédula es 1234567890")] });

        Assert.DoesNotContain(tags, tag => tag.Contains(TenantKey, StringComparison.Ordinal));
        Assert.DoesNotContain(tags, tag => tag.Contains("1234567890", StringComparison.Ordinal));
        Assert.DoesNotContain(tags, tag => tag.Contains("Eres el asistente", StringComparison.Ordinal));
        Assert.DoesNotContain(tags, tag => tag.Contains("Hola.", StringComparison.Ordinal));
    }

    private Task<AiModelResponse> CompleteAsync(AiModelRequest request) =>
        new ClaudeModelClient(new SingleHandlerFactory(_server), Microsoft.Extensions.Options.Options.Create(new AiModelCallOptions()), NullLogger<ClaudeModelClient>.Instance)
            .CompleteAsync(request, TestContext.Current.CancellationToken);

    private static AiModelRequest Request() =>
        new(
            new AiCredential(Guid.NewGuid(), TenantKey),
            "claude-opus-5-5",
            "medium",
            "Eres el asistente de la tienda.",
            null,
            "Fecha y hora del negocio: 2026-11-13 10:00",
            [new AiChatMessage(AiChatRole.User, "hola")],
            [],
            [Tool("send_reply")],
            16_000);

    private static AiToolDefinition Tool(string name) =>
        new(name, $"La herramienta {name}.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""");

    private static string Canonical(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }
}
