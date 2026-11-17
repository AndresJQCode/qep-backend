using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Model.OpenAi;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-10-29 §9.1 (P-1 para OpenAI): lo que <see cref="OpenAiModelClient"/> manda a la Responses API, contra un servidor falso que
/// captura el cuerpo. Las mismas exigencias de <see cref="ClaudeModelClientTests"/> que aplican a OpenAI (herramientas, contexto volátil
/// fuera del prefijo estable, errores tipados, clave por petición) más las de O-1..O-9: sin estado del lado del proveedor y append-only
/// con los ítems de salida tal cual.
/// </summary>
public sealed class OpenAiModelClientTests : IDisposable
{
    private const string TenantKey = "sk-proj-TEST-tenant-one";
    private const string OtherTenantKey = "sk-proj-TEST-tenant-two";

    private readonly FakeOpenAiServer _server = new();
    private readonly CapturingLogger _logger = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ItCallsTheResponsesApiWithTheModelInstructionsAndOutputCap()
    {
        await CompleteAsync(Request());

        var request = _server.Last;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/responses", request.Url);
        var body = request.Body;
        Assert.Equal("gpt-6-luna", body.GetProperty("model").GetString());
        Assert.Equal("Eres el asistente de la tienda.", body.GetProperty("instructions").GetString());
        Assert.Equal(16_000, body.GetProperty("max_output_tokens").GetInt32());
        Assert.False(body.TryGetProperty("stream", out var stream) && stream.GetBoolean());
    }

    // O-3: store vale true si se omite; sin estado del lado del proveedor, el razonamiento viaja cifrado.
    [Fact]
    public async Task ItKeepsNoStateOnTheProvider()
    {
        await CompleteAsync(Request());

        var body = _server.Last.Body;
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.False(body.TryGetProperty("previous_response_id", out _));
        Assert.False(body.TryGetProperty("conversation", out _));
        Assert.Contains("reasoning.encrypted_content", body.GetProperty("include").EnumerateArray().Select(item => item.GetString()));
    }

    // O-6: el esfuerzo sólo si la lista curada lo trae; sin él, el default del modelo.
    [Fact]
    public async Task TheEffortGoesOnlyWhenTheCuratedListHasIt()
    {
        await CompleteAsync(Request() with { Effort = "low" });
        await CompleteAsync(Request() with { Effort = null });

        var requests = _server.Requests.ToArray();
        Assert.Equal("low", requests[0].Body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(requests[1].Body.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task ToolsAreStrictFunctionsSortedByNameAndNeverForced()
    {
        await CompleteAsync(Request() with { Tools = [Tool("send_reply"), Tool("handoff_to_human"), Tool("search_catalog")] });

        var body = _server.Last.Body;
        var tools = body.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["handoff_to_human", "search_catalog", "send_reply"], tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.All(tools, tool => Assert.Equal("function", tool.GetProperty("type").GetString()));
        Assert.All(tools, tool => Assert.True(tool.GetProperty("strict").GetBoolean()));
        Assert.All(tools, tool => Assert.False(tool.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean()));
        Assert.Equal("La herramienta send_reply.", tools[2].GetProperty("description").GetString());
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task AnAnswerWithOnlyTextComesBackAsASendReplyWithoutSources()
    {
        _server.Respond = () => FakeOpenAiServer.Ok(FakeOpenAiServer.TextBody("Claro, te ayudo."));

        var response = await CompleteAsync(Request());

        Assert.Equal(AiStopReason.EndTurn, response.StopReason);
        Assert.Equal("Claro, te ayudo.", response.Text);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("send_reply", call.Name);
        var arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement;
        Assert.Equal("Claro, te ayudo.", arguments.GetProperty("text").GetString());
        Assert.Equal(0, arguments.GetProperty("sourceIds").GetArrayLength());
    }

    // O-5: el prefijo cacheable (herramientas, instrucciones, historial) no lleva lo que cambia en cada llamada.
    [Fact]
    public async Task TheVolatileContextGoesAfterTheHistoryAndNeverInTheInstructions()
    {
        await CompleteAsync(Request() with { VolatileContext = "Fecha y hora: 2026-11-13 10:00" });

        var body = _server.Last.Body;
        Assert.DoesNotContain("2026-11-13", body.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        var input = body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["user", "developer"], input.Select(item => item.GetProperty("role").GetString()));
        Assert.Contains("2026-11-13 10:00", input[^1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGreetingIsADeveloperMessageAfterTheFirstCustomerTurnAndTheHistoryNeverEndsInAssistant()
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

        var input = _server.Last.Body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["user", "developer", "assistant", "user", "developer"], input.Select(item => item.GetProperty("role").GetString()));
        Assert.Contains("¡Hola! Soy el asistente.", input[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("¿En qué te ayudo?", input[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task WithOnlyOneCustomerTurnTheGreetingAndTheVolatileContextShareOneDeveloperMessage()
    {
        await CompleteAsync(Request() with { GreetingSent = "¡Hola!" });

        var input = _server.Last.Body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["user", "developer"], input.Select(item => item.GetProperty("role").GetString()));
        Assert.Contains("¡Hola!", input[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Fecha", input[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    // O-4: los ítems de salida vuelven tal cual (el razonamiento cifrado sólo vale sin tocar), y un resultado por llamada.
    [Fact]
    public async Task ThePriorOutputItemsAreResentVerbatimFollowedByOneOutputPerCall()
    {
        const string reasoning = """{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"gAAAA+cifrado/opaco=="}""";
        const string callA = """{"type":"function_call","id":"fc_a","call_id":"call_a","name":"search_catalog","arguments":"{\"query\":\"jabón\",\"limit\":5}","status":"completed"}""";
        const string callB = """{"type":"function_call","id":"fc_b","call_id":"call_b","name":"get_order_status","arguments":"{\"orderNumber\":null}","status":"completed"}""";
        var round = new AiModelRound(
            [new AiOpaqueBlock(reasoning), new AiOpaqueBlock(callA), new AiOpaqueBlock(callB)],
            [new AiToolResult("call_a", """[{"name":"Jabón"}]""", false), new AiToolResult("call_b", """{"error":"customer_unknown"}""", true)]);

        await CompleteAsync(Request() with { PriorAssistantBlocks = [round] });

        var input = _server.Last.Body.GetProperty("input").EnumerateArray().ToArray();
        // Append-only: el contexto volátil quedó donde estaba en la primera llamada, antes de la vuelta.
        Assert.Equal("developer", input[1].GetProperty("role").GetString());
        Assert.Equal([Canonical(reasoning), Canonical(callA), Canonical(callB)], input[2..5].Select(item => Canonical(item.GetRawText())));
        var outputs = input[5..];
        Assert.All(outputs, item => Assert.Equal("function_call_output", item.GetProperty("type").GetString()));
        Assert.Equal(["call_a", "call_b"], outputs.Select(item => item.GetProperty("call_id").GetString()));
        Assert.Equal("""{"error":"customer_unknown"}""", outputs[1].GetProperty("output").GetString());
    }

    [Fact]
    public async Task AResponseBecomesToolCallsOpaqueItemsUsageAndServedModel()
    {
        _server.Respond = () => FakeOpenAiServer.Ok("""
            {"id":"resp_2","object":"response","status":"completed","model":"gpt-6-luna-2026-10-01","incomplete_details":null,"error":null,
             "output":[
               {"type":"reasoning","id":"rs_9","summary":[],"encrypted_content":"enc-1"},
               {"type":"message","id":"msg_9","status":"completed","role":"assistant","phase":"commentary",
                "content":[{"type":"output_text","text":"Busco eso.","annotations":[]}]},
               {"type":"function_call","id":"fc_9","call_id":"call_9","name":"search_catalog","arguments":"{\"query\":\"jabón\",\"limit\":3}","status":"completed"}],
             "usage":{"input_tokens":1200,"input_tokens_details":{"cached_tokens":1000},"output_tokens":45,
                      "output_tokens_details":{"reasoning_tokens":30},"total_tokens":1245}}
            """, requestId: "req_42");

        var response = await CompleteAsync(Request());

        Assert.Equal(AiStopReason.ToolUse, response.StopReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(("call_9", "search_catalog"), (call.Id, call.Name));
        Assert.Equal(3, JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("Busco eso.", response.Text);
        Assert.Equal(3, response.OpaqueAssistantBlocks.Count);
        Assert.Contains("enc-1", response.OpaqueAssistantBlocks[0].Json, StringComparison.Ordinal);
        Assert.Contains("commentary", response.OpaqueAssistantBlocks[1].Json, StringComparison.Ordinal);
        // O-7: OpenAI no reporta escritura en caché: null, no 0.
        Assert.Equal(new AiModelUsage(1200, 45, null, 1000), response.Usage);
        Assert.Equal("gpt-6-luna-2026-10-01", response.ServedModel);
        Assert.Equal("req_42", response.RequestId);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", null, typeof(AiInvalidRequestException))]
    [InlineData(413, "invalid_request_error", null, typeof(AiInvalidRequestException))]
    [InlineData(422, "invalid_request_error", null, typeof(AiInvalidRequestException))]
    [InlineData(401, "invalid_request_error", "invalid_api_key", typeof(AiCredentialsRejectedException))]
    [InlineData(401, "invalid_request_error", "ip_not_authorized", typeof(AiCredentialsRejectedException))]
    [InlineData(403, "invalid_request_error", "unsupported_country_region_territory", typeof(AiPermissionDeniedException))]
    [InlineData(404, "invalid_request_error", null, typeof(AiModelUnavailableException))]
    [InlineData(429, "requests", "rate_limit_exceeded", typeof(AiRateLimitedException))]
    [InlineData(429, "rate_limit_error", "slow_down", typeof(AiRateLimitedException))]
    [InlineData(429, "insufficient_quota", "insufficient_quota", typeof(AiQuotaExhaustedException))]
    [InlineData(429, "insufficient_quota", "credit_balance_exhausted", typeof(AiQuotaExhaustedException))]
    [InlineData(429, "insufficient_quota", "organization_spend_limit_exceeded", typeof(AiQuotaExhaustedException))]
    [InlineData(429, "insufficient_quota", "project_spend_limit_exceeded", typeof(AiQuotaExhaustedException))]
    [InlineData(429, "insufficient_quota", "organization_usage_limit_exceeded", typeof(AiQuotaExhaustedException))]
    [InlineData(429, "billing_error", "credit_balance_exhausted", typeof(AiQuotaExhaustedException))]
    [InlineData(408, null, null, typeof(AiProviderUnavailableException))]
    [InlineData(500, "server_error", null, typeof(AiProviderUnavailableException))]
    [InlineData(503, "service_unavailable_error", "server_is_overloaded", typeof(AiProviderUnavailableException))]
    public async Task EveryProviderErrorMapsToItsTypedError(int status, string? type, string? code, Type expected)
    {
        _server.Respond = () => FakeOpenAiServer.Error(status, type, code);

        var failure = await Assert.ThrowsAnyAsync<AiModelException>(() => CompleteAsync(Request()));

        Assert.IsType(expected, failure);
        Assert.Equal("req_err", failure.RequestId);
    }

    [Fact]
    public async Task ARateLimitCarriesItsRetryAfter()
    {
        _server.Respond = () => FakeOpenAiServer.Error(429, "rate_limit_error", "slow_down", retryAfter: "7");

        var failure = await Assert.ThrowsAsync<AiRateLimitedException>(() => CompleteAsync(Request()));

        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task ANetworkFailureIsProviderUnavailable()
    {
        _server.Throw = new HttpRequestException("connection refused");

        var failure = await Assert.ThrowsAsync<AiProviderUnavailableException>(() => CompleteAsync(Request()));

        Assert.True(failure.IsRetryable);
    }

    // El timeout de la llamada (C-13) no es una cancelación de quien llama: se reintenta.
    [Fact]
    public async Task ATimeoutIsProviderUnavailable()
    {
        _server.Throw = new TaskCanceledException("timed out", new TimeoutException());

        await Assert.ThrowsAsync<AiProviderUnavailableException>(() => CompleteAsync(Request()));
    }

    [Fact]
    public async Task ARefusalIsTypedAndKeepsTheUsage()
    {
        _server.Respond = () => FakeOpenAiServer.Ok("""
            {"id":"resp_3","object":"response","status":"completed","model":"gpt-6-luna","incomplete_details":null,"error":null,
             "output":[{"type":"message","id":"msg_3","status":"completed","role":"assistant",
                        "content":[{"type":"refusal","refusal":"No puedo ayudar con eso."}]}],
             "usage":{"input_tokens":50,"input_tokens_details":{"cached_tokens":0},"output_tokens":8,"total_tokens":58}}
            """);

        var failure = await Assert.ThrowsAsync<AiRefusedException>(() => CompleteAsync(Request()));

        Assert.Null(failure.Category);
        Assert.Equal(50, failure.Usage!.Input);
        Assert.Equal("gpt-6-luna", failure.ServedModel);
    }

    [Fact]
    public async Task AContentFilterCutIsARefusal()
    {
        _server.Respond = () => FakeOpenAiServer.Ok(Incomplete("content_filter"));

        var failure = await Assert.ThrowsAsync<AiRefusedException>(() => CompleteAsync(Request()));

        Assert.Equal("content_filter", failure.Category);
    }

    [Fact]
    public async Task AResponseCutByTheOutputCapIsAnInvalidResponseThatKeepsTheUsage()
    {
        _server.Respond = () => FakeOpenAiServer.Ok(Incomplete("max_output_tokens"));

        var failure = await Assert.ThrowsAsync<AiInvalidResponseException>(() => CompleteAsync(Request()));

        Assert.Equal("max_output_tokens", failure.Reason);
        Assert.Equal(16_000, failure.Usage!.Output);
    }

    [Theory]
    [InlineData("rate_limit_exceeded", typeof(AiRateLimitedException))]
    [InlineData("server_error", typeof(AiProviderUnavailableException))]
    [InlineData("invalid_prompt", typeof(AiInvalidRequestException))]
    public async Task AFailedResponseMapsItsErrorCode(string code, Type expected)
    {
        _server.Respond = () => FakeOpenAiServer.Ok($$"""
            {"id":"resp_5","object":"response","status":"failed","model":"gpt-6-luna","incomplete_details":null,
             "error":{"code":"{{code}}","message":"m"},"output":[],"usage":null}
            """);

        var failure = await Assert.ThrowsAnyAsync<AiModelException>(() => CompleteAsync(Request()));

        Assert.IsType(expected, failure);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsAnInvalidResponse()
    {
        _server.Respond = () => FakeOpenAiServer.Ok("<html>gateway</html>");

        await Assert.ThrowsAsync<AiInvalidResponseException>(() => CompleteAsync(Request()));
    }

    [Fact]
    public async Task AnAnswerWithoutTextNorCallsIsAnInvalidResponse()
    {
        _server.Respond = () => FakeOpenAiServer.Ok("""
            {"id":"resp_6","object":"response","status":"completed","model":"gpt-6-luna","incomplete_details":null,"error":null,
             "output":[{"type":"reasoning","id":"rs_6","summary":[],"encrypted_content":"enc"}],
             "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":0},"output_tokens":4,"total_tokens":14}}
            """);

        var failure = await Assert.ThrowsAsync<AiInvalidResponseException>(() => CompleteAsync(Request()));

        Assert.Equal(4, failure.Usage!.Output);
    }

    [Fact]
    public async Task EachRequestUsesTheKeyOfItsOwnCredential()
    {
        await CompleteAsync(Request());
        await CompleteAsync(Request() with { Credential = new AiCredential(Guid.NewGuid(), OtherTenantKey) });

        var requests = _server.Requests.ToArray();
        Assert.All(requests, request => Assert.Equal("Bearer", request.Scheme));
        Assert.True(requests[0].Key == TenantKey, "The first request must carry the first tenant's key.");
        Assert.True(requests[1].Key == OtherTenantKey, "The second request must carry the second tenant's key, not a shared one.");
    }

    // D-A29: el log lleva status, tipo, código e id; nunca la clave, el cuerpo de la respuesta ni el texto de la conversación.
    [Fact]
    public async Task TheLogsCarryTheErrorButNeverTheKeyTheBodyNorTheConversation()
    {
        _server.Respond = () => FakeOpenAiServer.Error(400, "invalid_request_error", "bad", message: "echo: mi cédula es 1234567890");

        await Assert.ThrowsAsync<AiInvalidRequestException>(
            () => CompleteAsync(Request() with { Messages = [new AiChatMessage(AiChatRole.User, "mi cédula es 1234567890")] }));
        _server.Respond = () => FakeOpenAiServer.Ok(FakeOpenAiServer.TextBody("Hola."));
        await CompleteAsync(Request());

        var text = string.Join("\n", _logger.Entries);
        Assert.Contains("400", text, StringComparison.Ordinal);
        Assert.Contains("req_err", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Eres el asistente", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hola.", text, StringComparison.Ordinal);
    }

    private static string Incomplete(string reason) => $$$"""
        {"id":"resp_4","object":"response","status":"incomplete","model":"gpt-6-luna","incomplete_details":{"reason":"{{{reason}}}"},"error":null,
         "output":[{"type":"message","id":"msg_4","status":"incomplete","role":"assistant",
                    "content":[{"type":"output_text","text":"Te cuen","annotations":[]}]}],
         "usage":{"input_tokens":50,"input_tokens_details":{"cached_tokens":0},"output_tokens":16000,"total_tokens":16050}}
        """;

    private Task<AiModelResponse> CompleteAsync(AiModelRequest request) =>
        new OpenAiModelClient(new SingleHandlerFactory(_server), Microsoft.Extensions.Options.Options.Create(new AiModelCallOptions()), _logger)
            .CompleteAsync(request, TestContext.Current.CancellationToken);

    private static AiModelRequest Request() =>
        new(
            new AiCredential(Guid.NewGuid(), TenantKey),
            "gpt-6-luna",
            null,
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

    private sealed class CapturingLogger : ILogger<OpenAiModelClient>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue($"{formatter(state, exception)} {exception}");
    }
}
