using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Eval;

/// <summary>The hard cap on model calls for a whole run (agent and judge): the owner approves a budget, this enforces it.</summary>
internal sealed class ModelCallBudget(int max)
{
    private int _used;

    public int Max { get; } = max;

    public int Used => _used;

    public void Take()
    {
        if (Interlocked.Increment(ref _used) > Max)
        {
            throw new EvalBudgetExceededException($"The run reached its cap of {Max} model calls.");
        }
    }
}

public sealed class EvalBudgetExceededException : Exception
{
    public EvalBudgetExceededException()
    {
    }

    public EvalBudgetExceededException(string message)
        : base(message)
    {
    }

    public EvalBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Counts every call against the budget before it leaves, so the cap is never passed by one call.</summary>
internal sealed class BudgetedModelClient(IAiModelClient inner, ModelCallBudget budget) : IAiModelClient
{
    public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        budget.Take();
        return inner.CompleteAsync(request, cancellationToken);
    }
}

/// <summary>One adapter per provider, as the production resolver; no fallback to another provider (C-20).</summary>
internal sealed class EvalModelClientResolver(IReadOnlyDictionary<AiProvider, IAiModelClient> clients) : IAiModelClientResolver
{
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Implements the port's name.")]
    public IAiModelClient For(AiProvider provider) =>
        clients.TryGetValue(provider, out var client)
            ? client
            : throw new InvalidOperationException($"The eval has no client for provider '{provider}'.");
}

/// <summary>A judge's verdict on one criterion the rules can't check.</summary>
internal sealed record JudgeVerdict(bool Pass, string Reason, AiModelUsage? Usage);

/// <summary>The hook for criteria that need judgement (spec of Task 10). The rules run first; the judge only sees cases they passed.</summary>
internal interface IEvalJudge
{
    string Label { get; }

    Task<JudgeVerdict> JudgeAsync(EvalCase @case, string outcomeDescription, CancellationToken cancellationToken);
}

/// <summary>
/// A judge on any provider, through the same <see cref="IAiModelClient"/> port. It asks for one JSON object in plain text and reads it
/// from the text or from the <c>send_reply</c> the adapters build from a text-only answer (C-8). An answer that isn't that JSON fails the
/// case: an unreadable grade is not a pass.
/// </summary>
internal sealed class ModelJudge(IAiModelClient client, AiCredential credential, AiProvider provider, string model, string? effort)
    : IEvalJudge
{
    private const string SystemPrompt =
        """
        You grade one turn of a customer-service chat agent for a hardware store in Colombia. The conversation is in Spanish.
        You receive the conversation, what the agent did on its turn, and one criterion. Decide only whether the agent's turn meets
        that criterion; ignore everything else. Answer with a single JSON object and nothing else:
        {"pass": true or false, "reason": "one short sentence"}
        """;

    public string Label { get; } = $"{provider.Value}:{model}{(effort is null ? string.Empty : $":{effort}")}";

    public async Task<JudgeVerdict> JudgeAsync(EvalCase @case, string outcomeDescription, CancellationToken cancellationToken)
    {
        var transcript = string.Join(
            "\n",
            @case.History.Select(turn => $"{turn.Role}: {(turn.Text ?? string.Empty)}{(turn.Kind is null or "text" ? string.Empty : $" [{turn.Kind}]")}"));
        var prompt =
            $"""
            Conversation:
            {transcript}

            The agent's turn:
            {outcomeDescription}

            Criterion (Spanish):
            {@case.Expect.Judge}
            """;
        var response = await client.CompleteAsync(
            new AiModelRequest(
                credential, model, effort, SystemPrompt, GreetingSent: null, VolatileContext: string.Empty,
                [new AiChatMessage(AiChatRole.User, prompt)], [], [], MaxOutputTokens: 2_000),
            cancellationToken);
        var text = response.Text ?? response.ToolCalls.Select(TextOf).FirstOrDefault(value => value is not null);
        return Parse(text) is { } verdict
            ? verdict with { Usage = response.Usage }
            : new JudgeVerdict(false, "judge_unparseable", response.Usage);
    }

    private static string? TextOf(AiToolCall call)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return arguments.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    }

    private static JudgeVerdict? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            return root.TryGetProperty("pass", out var pass) && pass.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new JudgeVerdict(
                    pass.GetBoolean(),
                    root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString()! : string.Empty,
                    null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
