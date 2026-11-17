using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Plan 2026-11-12, Review Focus 2 y D-A29, para OpenAI (spec §9.1): con el host en <c>Trace</c> (el nivel en que <see cref="HttpClient"/>
/// registra headers), ni la clave de OpenAI del tenant ni el texto del cliente aparecen en los logs, en la auditoría, en las tablas de
/// ejecución de <c>ai</c> ni en las trazas. Mismo recorrido que <see cref="AnthropicNoLeakTests"/>: un agente de OpenAI en el playground,
/// con una herramienta y una respuesta.
/// </summary>
public sealed class OpenAiNoLeakTests
{
    private const string CustomerText = "mi cédula es 1019876543 SENTINEL-CLIENTE";

    [Fact]
    public async Task TheKeyAndTheCustomerTextNeverAppearInLogsAuditAiTablesOrSpans()
    {
        await using var database = await StartDatabaseAsync();
        var logs = new CapturingLoggerProvider();
        var spans = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                spans.Enqueue(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    spans.Enqueue($"{tag.Key}={tag.Value}");
                }

                foreach (var @event in activity.Events)
                {
                    spans.Enqueue(@event.Name);
                    foreach (var tag in @event.Tags)
                    {
                        spans.Enqueue($"{tag.Key}={tag.Value}");
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var openAi = new FakeOpenAiResponsesHandler(OpenAiTestKey);
        openAi.Enqueue(FakeOpenAiResponsesHandler.FunctionCall("call_01", "search_catalog", """{"query":"jabón","limit":2}"""));
        openAi.Enqueue(FakeOpenAiResponsesHandler.Text("Con gusto te ayudo."));
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                AgentPlaygroundApiTests.UseFakeOpenAi(services, openAi);
                services.AddSingleton<ILoggerProvider>(logs);
            },
            new Dictionary<string, string>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:System"] = "Trace",
                ["Logging:LogLevel:System.Net.Http"] = "Trace",
            });
        await SeedTenantAsync(factory);
        await SeedOpenAiConnectionAsync(factory);
        using var client = CreateClient(factory, permissions: [AiPermissions.AgentRead, AiPermissions.AgentManage]);
        var agentId = await AgentPlaygroundApiTests.DraftAgentAsync(client, provider: "openai", model: "gpt-6-luna");
        var opened = await SendAsync(client, HttpMethod.Post, $"{AiUrl()}/agents/{agentId}/tests");
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var testId = (await JsonAsync(opened)).GetProperty("id").GetGuid();

        var sent = await SendAsync(client, HttpMethod.Post, $"{AiUrl()}/tests/{testId}/messages", new { text = CustomerText });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var test = await AgentPlaygroundApiTests.WaitUntilAnsweredAsync(client, testId);
        Assert.Equal("agent", test.GetProperty("transcript").EnumerateArray().Last().GetProperty("role").GetString());

        // Sanidad: la prueba de verdad pasó por el adaptador de OpenAI (con la clave del tenant, sin estado), por el log en Trace y por
        // los spans del host, y dejó el consumo de las dos llamadas a nombre de OpenAI. Sin SDK no hay spans propios del proveedor, y el
        // de HttpClient lo emite el handler primario, que aquí es el de mentira: lo que se vigila son los del resto del host.
        Assert.Equal([("/v1/responses", true, true), ("/v1/responses", true, true)], openAi.Requests);
        Assert.NotEmpty(logs.Lines);
        Assert.NotEmpty(spans);
        Assert.Equal(2L, await ScalarAsync<long>(
            database.GetConnectionString(),
            "SELECT count(*) FROM ai.usage_records WHERE test_id = @test AND provider = 'openai' AND model = 'gpt-6-luna'",
            ("test", testId)));

        foreach (var needle in new[] { OpenAiTestKey, "SENTINEL-CLIENTE", "1019876543" })
        {
            Assert.False(logs.Lines.Any(line => line.Contains(needle, StringComparison.Ordinal)), $"A log line carries «{Mask(needle)}».");
            Assert.False(spans.Any(span => span.Contains(needle, StringComparison.Ordinal)), $"A span carries «{Mask(needle)}».");
            Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "audit.entries", needle));
            Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "ai.usage_records", needle));
            Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "ai.tool_calls", needle));
            Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "ai.model_jobs", needle));
            Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "ai.agents", needle));
        }

        Assert.Equal(0L, await CountAsync(database.GetConnectionString(), "ai.tests", OpenAiTestKey));
    }

    private static Task<long> CountAsync(string connectionString, string table, string needle) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM {table} AS row WHERE row::text LIKE '%' || @needle || '%'", ("needle", needle));

    // El mensaje de una prueba roja tampoco imprime el centinela entero.
    private static string Mask(string value) => value.Length <= 6 ? "***" : $"{value[..4]}***";
}
