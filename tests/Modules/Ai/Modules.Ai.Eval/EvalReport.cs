using System.Globalization;
using System.Text.Json;
using Modules.Ai.Domain;

namespace Modules.Ai.Eval;

/// <summary>
/// Prices per million tokens in USD, by <c>provider:model</c>. QEP keeps no price table (owner, P-15), so the file comes from whoever
/// runs the eval, copied from each provider's pricing page on the day of the run. A component with tokens and no price leaves the cost
/// unknown instead of guessing it.
/// </summary>
internal sealed class ModelPrice
{
    public decimal? InputUsdPerMillion { get; set; }

    public decimal? OutputUsdPerMillion { get; set; }

    public decimal? CacheWriteUsdPerMillion { get; set; }

    public decimal? CacheReadUsdPerMillion { get; set; }
}

internal static class EvalPrices
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public static Dictionary<string, ModelPrice> Load(string? path) =>
        path is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(File.ReadAllText(path), Json) ?? [];

    /// <summary>
    /// The cost of one call, or <c>null</c> if a price is missing. The adapters report usage the way each provider does: Anthropic's
    /// input excludes cache writes and reads (they come apart), OpenAI's input includes the cached tokens (O-7), so they are subtracted.
    /// The price is looked up by the model that served the call (C-9 can serve another) and falls back to the requested one.
    /// </summary>
    public static decimal? CostOf(AiProvider provider, string requestedModel, EvalCall call, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        if (!prices.TryGetValue($"{provider.Value}:{call.ServedModel}", out var price)
            && !prices.TryGetValue($"{provider.Value}:{requestedModel}", out price))
        {
            return null;
        }

        var read = call.CacheRead ?? 0;
        var write = call.CacheCreation ?? 0;
        var uncached = provider == AiProviders.OpenAi ? Math.Max(0, call.Input - read) : call.Input;
        (long Tokens, decimal? Price)[] parts =
        [
            (uncached, price.InputUsdPerMillion),
            (call.Output, price.OutputUsdPerMillion),
            (write, price.CacheWriteUsdPerMillion),
            (read, price.CacheReadUsdPerMillion),
        ];
        if (parts.Any(part => part.Tokens > 0 && part.Price is null))
        {
            return null;
        }

        return parts.Sum(part => part.Tokens * (part.Price ?? 0m)) / 1_000_000m;
    }
}

internal sealed record VariantSummary(
    string Variant,
    int Cases,
    int Resolved,
    int RuleFailures,
    int JudgeFailures,
    int JudgePending,
    int Errors,
    double Quality,
    long? LatencyP50Ms,
    long? LatencyP90Ms,
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    long? CacheCreationTokens,
    long? CacheReadTokens,
    decimal? CostUsd,
    decimal? CostPerResolvedUsd,
    decimal? JudgeCostUsd,
    string CacheCheck,
    IReadOnlyList<string> CacheCheckFailures,
    IReadOnlyDictionary<string, string> QualityByCategory,
    string? AccountFailure);

internal static class EvalReport
{
    public const string CacheVerified = "verified";
    public const string CacheFailed = "failed";
    public const string CacheNotVerifiable = "not_verifiable";
    public const string CacheNotApplicable = "not_applicable";

    public static VariantSummary Summarize(
        EvalVariant variant, IReadOnlyList<EvalCaseResult> results, IReadOnlyDictionary<string, ModelPrice> prices, EvalVariant? judge,
        string? accountFailure)
    {
        var finished = results.Where(result => result.Outcome is "reply" or "handoff").ToList();
        var latencies = finished.Select(result => result.LatencyMs).Order().ToList();
        var calls = results.SelectMany(result => result.Calls).ToList();
        var costs = calls.Select(call => EvalPrices.CostOf(variant.Provider, variant.Model, call, prices)).ToList();
        decimal? cost = calls.Count > 0 && costs.All(value => value is not null) ? costs.Sum() : null;
        var resolved = results.Count(result => result.Resolved);
        var judgeCalls = results.Where(result => result.JudgeCall is not null).Select(result => result.JudgeCall!).ToList();
        var judgeCosts = judge is null ? [] : judgeCalls.Select(call => EvalPrices.CostOf(judge.Provider, judge.Model, call, prices)).ToList();
        var (cacheCheck, cacheFailures) = CacheCheckOf(variant, results);

        return new VariantSummary(
            variant.Label,
            results.Count,
            resolved,
            results.Count(result => result.RuleFailures.Count > 0),
            results.Count(result => result.JudgePass is false),
            results.Count(result => result.JudgePending),
            results.Count(result => result.Outcome is not ("reply" or "handoff")),
            results.Count == 0 ? 0 : (double)resolved / results.Count,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.9),
            calls.Count,
            calls.Sum(call => call.Input),
            calls.Sum(call => call.Output),
            calls.Any(call => call.CacheCreation is not null) ? calls.Sum(call => call.CacheCreation ?? 0) : null,
            calls.Any(call => call.CacheRead is not null) ? calls.Sum(call => call.CacheRead ?? 0) : null,
            cost,
            cost is { } total && resolved > 0 ? total / resolved : null,
            judgeCosts.Count > 0 && judgeCosts.All(value => value is not null) ? judgeCosts.Sum() : null,
            cacheCheck,
            cacheFailures,
            results.GroupBy(result => result.Category, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => $"{group.Count(result => result.Resolved)}/{group.Count()}", StringComparer.Ordinal),
            accountFailure);
    }

    /// <summary>
    /// C-12: from the second call of an Anthropic job on, part of the prompt must come from the cache (<c>CacheReadInputTokens &gt; 0</c>).
    /// Only jobs with two or more calls (a tool round) can show it; a run without any is not proof either way.
    /// </summary>
    private static (string Status, IReadOnlyList<string> Failures) CacheCheckOf(EvalVariant variant, IReadOnlyList<EvalCaseResult> results)
    {
        if (variant.Provider != AiProviders.Anthropic)
        {
            return (CacheNotApplicable, []);
        }

        var multiCall = results.Where(result => result.Calls.Count >= 2).ToList();
        if (multiCall.Count == 0)
        {
            return (CacheNotVerifiable, []);
        }

        var failures = multiCall
            .Where(result => result.Calls.Skip(1).Any(call => (call.CacheRead ?? 0) <= 0))
            .Select(result => $"{result.CaseId}#{result.Repeat}")
            .ToList();
        return (failures.Count == 0 ? CacheVerified : CacheFailed, failures);
    }

    /// <summary>Nearest rank.</summary>
    private static long? Percentile(List<long> sorted, double percentile) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static void Print(VariantSummary summary, IReadOnlyList<EvalCaseResult> results, TextWriter output)
    {
        var invariant = CultureInfo.InvariantCulture;
        output.WriteLine();
        output.WriteLine($"== {summary.Variant} ==");
        output.WriteLine(string.Create(invariant, $"  quality          {summary.Resolved}/{summary.Cases} resolved ({summary.Quality:P1}); rule failures {summary.RuleFailures}, judge failures {summary.JudgeFailures}, judge pending {summary.JudgePending}, errors {summary.Errors}"));
        output.WriteLine(string.Create(invariant, $"  latency          p50 {summary.LatencyP50Ms?.ToString(invariant) ?? "-"} ms, p90 {summary.LatencyP90Ms?.ToString(invariant) ?? "-"} ms"));
        output.WriteLine(string.Create(invariant, $"  tokens           {summary.ModelCalls} calls; input {summary.InputTokens}, output {summary.OutputTokens}, cache write {summary.CacheCreationTokens?.ToString(invariant) ?? "n/r"}, cache read {summary.CacheReadTokens?.ToString(invariant) ?? "n/r"}"));
        output.WriteLine(string.Create(invariant, $"  cost             {Usd(summary.CostUsd)} total, {Usd(summary.CostPerResolvedUsd)} per resolved conversation; judge {Usd(summary.JudgeCostUsd)}"));
        output.WriteLine($"  C-12 cache check {summary.CacheCheck}{(summary.CacheCheckFailures.Count == 0 ? string.Empty : $" ({string.Join(", ", summary.CacheCheckFailures)})")}");
        output.WriteLine($"  by category      {string.Join(", ", summary.QualityByCategory.Select(pair => $"{pair.Key} {pair.Value}"))}");
        if (summary.AccountFailure is not null)
        {
            output.WriteLine($"  STOPPED          the provider rejected the account: {summary.AccountFailure}");
        }

        foreach (var result in results.Where(result => !result.Resolved))
        {
            var why = result.RuleFailures.Count > 0
                ? string.Join("; ", result.RuleFailures)
                : result.JudgePass is false ? $"judge: {result.JudgeReason}" : $"{result.Outcome} {result.Reason}";
            output.WriteLine($"  x {result.CaseId}#{result.Repeat}: {why}");
        }

        static string Usd(decimal? value) => value is { } amount ? string.Create(CultureInfo.InvariantCulture, $"US${amount:0.0000}") : "unknown (no prices)";
    }
}
