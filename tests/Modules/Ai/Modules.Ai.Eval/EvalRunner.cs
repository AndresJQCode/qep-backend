using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Eval;

/// <summary>A provider, a model and an effort to run the whole set with (<c>anthropic:claude-opus-5-5:low</c>).</summary>
internal sealed record EvalVariant(AiProvider Provider, string Model, string? Effort)
{
    public string Label => $"{Provider.Value}:{Model}{(Effort is null ? string.Empty : $":{Effort}")}";

    public static bool TryParse(string text, out EvalVariant? variant)
    {
        variant = null;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || !AiProvider.TryParse(parts[0], out var provider) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        variant = new EvalVariant(provider!, parts[1], parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null);
        return true;
    }
}

/// <summary>One model call of a case, as the runner recorded it (C-4).</summary>
internal sealed record EvalCall(string? ServedModel, long Input, long Output, long? CacheCreation, long? CacheRead, string Outcome);

internal sealed class EvalCaseResult
{
    public required string CaseId { get; init; }

    public required string Category { get; init; }

    public int Repeat { get; init; }

    /// <summary><c>reply</c>, <c>handoff</c>, <c>failed</c>, <c>retry</c> (provider unavailable or rate-limited) or <c>error</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The handoff reason, the failure reason and code, or the error type.</summary>
    public string? Reason { get; init; }

    public string? Reply { get; init; }

    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public IReadOnlyList<string> Tools { get; init; } = [];

    public long LatencyMs { get; init; }

    public IReadOnlyList<EvalCall> Calls { get; init; } = [];

    public List<string> RuleFailures { get; init; } = [];

    public bool? JudgePass { get; set; }

    public string? JudgeReason { get; set; }

    public EvalCall? JudgeCall { get; set; }

    /// <summary>A resolved conversation: every rule passed and, if the case has a rubric and a judge ran, the judge passed too.</summary>
    public bool Resolved => Outcome is "reply" or "handoff" && RuleFailures.Count == 0 && JudgePass is not false;

    /// <summary>The rules passed and the case has a rubric, but no judge was configured: graded only by rules.</summary>
    public bool JudgePending { get; set; }
}

/// <summary>
/// Runs every case of the set through the <b>same</b> <see cref="AgentTurnRunner"/> production uses, with the provider of the variant,
/// so a comparison measures the model and not two harnesses (plan, Task 10). Cases run one after the other: a rate limit would otherwise
/// show up as a quality loss.
/// </summary>
internal sealed class EvalRunner(
    IAiModelClientResolver resolver,
    AiRunLimits limits,
    AiCredential credential,
    IEvalJudge? judge,
    ILoggerFactory loggers)
{
    private readonly EvalLedger _ledger = new();
    private readonly EvalCredentials _credentials = new();
    private readonly EvalClock _clock = new();

    /// <summary>Set when the provider rejected the account (key, permission or credit): the rest of the variant would fail the same way.</summary>
    public string? AccountFailure => _credentials.AccountFailure;

    public async Task<EvalCaseResult> RunAsync(EvalVariant variant, EvalCase @case, int repeat, CancellationToken cancellationToken)
    {
        var runner = new AgentTurnRunner(
            resolver,
            [new SearchCatalogTool(new EvalCatalog()), new GetOrderStatusTool(new EvalOrders()), new SimulatedQuotationDraftTool()],
            _ledger,
            _credentials,
            limits,
            _clock,
            loggers.CreateLogger<AgentTurnRunner>());
        var config = EvalFixture.ConfigFor(variant.Provider, variant.Model);
        var prompt = new AgentPromptBuilder(limits).Build(new AgentPromptInput(
            config,
            [],
            [.. @case.History.Select(turn => turn.ToHistoryTurn())],
            new TenantCalendar(_clock.UtcNow, EvalFixture.Bogota),
            WindowRemaining: TimeSpan.FromHours(23)));
        var jobId = Guid.CreateVersion7();
        var context = new AgentTurnContext(
            EvalFixture.TenantId, EvalFixture.AgentId, AgentVersionId: null, variant.Provider, variant.Model, variant.Effort, config,
            credential, jobId, SessionId: null, ConversationId: null, TestId: null,
            @case.Customer == EvalSetValidator.KnownCustomer ? EvalFixture.KnownCustomerId : null,
            EvalFixture.KnownSourceIds,
            // The playground mode: create_quotation_draft is the simulated one, nothing is written anywhere.
            IsTest: true);

        var watch = Stopwatch.StartNew();
        AgentTurnOutcome? outcome = null;
        string? error = null;
        try
        {
            outcome = await runner.RunAsync(context, prompt, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or EvalBudgetExceededException))
        {
            // Only the type: a message could carry provider text. The id of the request is in the adapter's warning log.
            error = exception.GetType().Name;
        }

        watch.Stop();
        var calls = _ledger.UsageOf(jobId)
            .Select(record => new EvalCall(
                record.ServedModel, record.Usage.Input, record.Usage.Output, record.Usage.CacheCreation, record.Usage.CacheRead,
                record.Outcome))
            .ToList();
        var tools = _ledger.ToolCallsOf(jobId).Select(record => record.Tool).ToList();
        (string Name, string? Reason, string? Reply, IReadOnlyList<string> Sources) ended = outcome switch
        {
            AgentReplied replied => ("reply", null, replied.Text, replied.SourceIds),
            AgentHandedOff handedOff => ("handoff", handedOff.Reason, null, []),
            AgentTurnFailed failed => ("failed", $"{failed.Kind}/{failed.Reason}/{failed.Code}", null, []),
            AgentTurnRetry retry => ("retry", retry.Code, null, []),
            _ => ("error", error, null, []),
        };
        var result = new EvalCaseResult
        {
            CaseId = @case.Id,
            Category = @case.Category,
            Repeat = repeat,
            Outcome = ended.Name,
            Reason = ended.Reason,
            Reply = ended.Reply,
            SourceIds = ended.Sources,
            Tools = tools,
            LatencyMs = watch.ElapsedMilliseconds,
            Calls = calls,
            RuleFailures = [.. EvalGrader.Grade(@case, ended.Name, ended.Reason, ended.Reply, ended.Sources, tools)],
        };

        if (result.RuleFailures.Count == 0 && @case.Expect.Judge is not null)
        {
            if (judge is null)
            {
                result.JudgePending = true;
            }
            else
            {
                var verdict = await judge.JudgeAsync(@case, Describe(result), cancellationToken);
                result.JudgePass = verdict.Pass;
                result.JudgeReason = verdict.Reason;
                result.JudgeCall = verdict.Usage is { } usage
                    ? new EvalCall(null, usage.Input, usage.Output, usage.CacheCreation, usage.CacheRead, AiRunOutcomes.Ok)
                    : null;
            }
        }

        return result;
    }

    private static string Describe(EvalCaseResult result) =>
        result.Outcome == "reply"
            ? $"It replied to the customer: «{result.Reply}»"
            : $"It handed the conversation to a person of the team (reason: {result.Reason}) without replying.";
}

/// <summary>The rule checks: what can be decided without a model (final outcome, handoff reason, tools, sources, never reveals).</summary>
internal static partial class EvalGrader
{
    public static IEnumerable<string> Grade(
        EvalCase @case, string outcome, string? reason, string? reply, IReadOnlyList<string> sources, IReadOnlyList<string> tools)
    {
        var expect = @case.Expect;
        if (outcome is not ("reply" or "handoff"))
        {
            yield return $"ended in {outcome} ({reason})";
            yield break;
        }

        if (expect.Outcome != EvalExpectation.ReplyOrHandoff && expect.Outcome != outcome)
        {
            yield return $"expected {expect.Outcome}, got {outcome}{(reason is null ? string.Empty : $" ({reason})")}";
        }

        if (outcome == "handoff" && expect.HandoffReasons.Count > 0 && !expect.HandoffReasons.Contains(reason ?? string.Empty))
        {
            yield return $"handoff reason {reason} not in [{string.Join(", ", expect.HandoffReasons)}]";
        }

        foreach (var tool in expect.RequiredTools.Where(tool => !tools.Contains(tool)))
        {
            yield return $"did not call {tool}";
        }

        foreach (var tool in expect.ForbiddenTools.Where(tools.Contains))
        {
            yield return $"called forbidden {tool}";
        }

        if (reply is null)
        {
            yield break;
        }

        var folded = Fold(reply);
        foreach (var secret in EvalFixture.NeverReveal.Concat(expect.MustNotContain).Where(text => folded.Contains(Fold(text), StringComparison.Ordinal)))
        {
            yield return $"reply contains forbidden «{secret}»";
        }

        if (outcome != "reply")
        {
            yield break;
        }

        if (expect.SourcesAny.Count > 0 && !expect.SourcesAny.Any(sources.Contains))
        {
            yield return $"cited none of [{string.Join(", ", expect.SourcesAny)}]";
        }

        if (expect.MustContainAny.Count > 0 && !expect.MustContainAny.Any(text => folded.Contains(Fold(text), StringComparison.Ordinal)))
        {
            yield return $"reply has none of [{string.Join(", ", expect.MustContainAny)}]";
        }
    }

    /// <summary>Lower case, without accents and without thousands separators, so «$289.900» and «289900» compare equal.</summary>
    internal static string Fold(string text) => ThousandsSeparator().Replace(EvalCatalog.Normalize(text), string.Empty);

    [GeneratedRegex(@"(?<=\d)[.,](?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();
}
