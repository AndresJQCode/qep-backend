using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C13 y D-C15, plan fase 2 C3.5, como <see cref="AnthropicNoLeakTests"/>: con el host en <c>Trace</c>, ni la
/// clave de Anthropic del tenant ni el texto de la pregunta o de la respuesta aparecen en los logs, en las trazas, en la auditoría, en el
/// consumo ni en los <c>tool_calls</c>. La pregunta y la respuesta viven sólo en el hilo (<c>ai.copilot_messages</c>), y la respuesta ya
/// pagada en <c>model_jobs.result</c> hasta su commit.
/// </summary>
public sealed class CopilotNoLeakTests
{
    private const string Question = "¿Cuánto le vendimos a SENTINEL-PREGUNTA?";
    private const string AnswerText = "Le vendimos SENTINEL-RESPUESTA.";

    [Fact]
    public async Task TheQuestionTheAnswerAndTheKeyNeverAppearInLogs()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
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
            },
        };
        ActivitySource.AddActivityListener(listener);
        var anthropic = new ScriptedAnthropicHandler();
        anthropic.Enqueue(_ => FakeAnthropicMessagesHandler.ToolUse(
            "toolu_1", "get_orders_summary", """{"from":null,"to":null,"advisorId":null,"clientId":null,"paymentStatus":null,"source":null}"""));
        anthropic.Enqueue(_ => CopilotHarness.Answer(AnswerText));
        using var factory = new QepApiFactory(
            connectionString,
            services =>
            {
                AgentPlaygroundApiTests.UseFakeAnthropic(services, anthropic);
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
        await CopilotHarness.EnableAsync(factory);

        var thread = await CopilotHarness.AskAndWaitAsync(factory, Question);

        // Sanidad: la respuesta llegó, con la clave correcta, y el log de verdad estaba en Trace.
        Assert.Equal(AnswerText, thread.GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString());
        Assert.All(anthropic.KeyMatched, Assert.True);
        Assert.NotEmpty(logs.Lines);

        foreach (var needle in new[] { AnthropicTestKey, "SENTINEL-PREGUNTA", "SENTINEL-RESPUESTA" })
        {
            Assert.False(logs.Lines.Any(line => line.Contains(needle, StringComparison.Ordinal)), $"A log line carries «{Mask(needle)}».");
            Assert.False(spans.Any(span => span.Contains(needle, StringComparison.Ordinal)), $"A span carries «{Mask(needle)}».");
            Assert.Equal(0L, await CountAsync(connectionString, "audit.entries", needle));
            Assert.Equal(0L, await CountAsync(connectionString, "ai.usage_records", needle));
            Assert.Equal(0L, await CountAsync(connectionString, "ai.tool_calls", needle));
            Assert.Equal(0L, await CountAsync(connectionString, "ai.copilot_settings", needle));
        }

        // La clave tampoco quedó en el hilo ni en la cola.
        Assert.Equal(0L, await CountAsync(connectionString, "ai.copilot_messages", AnthropicTestKey));
        Assert.Equal(0L, await CountAsync(connectionString, "ai.model_jobs", AnthropicTestKey));
        Assert.Equal(0L, await CountAsync(connectionString, "ai.model_jobs", "SENTINEL-PREGUNTA"));
    }

    private static Task<long> CountAsync(string connectionString, string table, string needle) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM {table} AS row WHERE row::text LIKE '%' || @needle || '%'", ("needle", needle));

    // El mensaje de una prueba roja tampoco imprime el centinela entero.
    private static string Mask(string value) => value.Length <= 6 ? "***" : $"{value[..4]}***";
}
