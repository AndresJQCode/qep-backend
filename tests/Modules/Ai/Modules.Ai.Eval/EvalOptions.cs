using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Modules.Ai.Domain;

namespace Modules.Ai.Eval;

/// <summary>The command line of the eval. Keys never come from arguments (they would stay in the shell history): only from the
/// environment, by <see cref="KeyVariableOf"/>.</summary>
internal sealed class EvalOptions
{
    public const string Usage =
        """
        Usage:
          dotnet run --project tests/Modules/Ai/Modules.Ai.Eval -- --dry-run
          dotnet run --project tests/Modules/Ai/Modules.Ai.Eval -- --confirm-spend --max-model-calls <n>
              --variant <provider:model[:effort]> [--variant ...] [--judge <provider:model[:effort]>]
              [--repeat <n>] [--cases <id-or-category,...>] [--prices <prices.json>] [--set <eval-set.json>] [--out <report.json>]
        Keys, by name: QEP_EVAL_ANTHROPIC_API_KEY, QEP_EVAL_OPENAI_API_KEY (only the providers the run uses).
        """;

    public static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The replies are Spanish: keep accents readable in the report.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool DryRun { get; private set; }

    public bool ConfirmSpend { get; private set; }

    public int? MaxModelCalls { get; private set; }

    public int Repeat { get; private set; } = 1;

    public List<EvalVariant> Variants { get; } = [];

    public EvalVariant? Judge { get; private set; }

    public HashSet<string> CaseFilter { get; } = new(StringComparer.Ordinal);

    public string? PricesPath { get; private set; }

    public string? SetPath { get; private set; }

    public string? OutPath { get; private set; }

    public List<string> Errors { get; } = [];

    public static string KeyVariableOf(AiProvider provider) => $"QEP_EVAL_{provider.Value.ToUpperInvariant()}_API_KEY";

    public static EvalOptions Parse(IReadOnlyList<string> args)
    {
        var options = new EvalOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            string? Next() => index + 1 < args.Count ? args[++index] : null;
            switch (name)
            {
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--confirm-spend":
                    options.ConfirmSpend = true;
                    break;
                case "--max-model-calls":
                    options.MaxModelCalls = PositiveInt(options, name, Next());
                    break;
                case "--repeat":
                    options.Repeat = PositiveInt(options, name, Next()) ?? 1;
                    break;
                case "--variant":
                    if (Variant(options, name, Next()) is { } variant)
                    {
                        options.Variants.Add(variant);
                    }

                    break;
                case "--judge":
                    options.Judge = Variant(options, name, Next());
                    break;
                case "--cases":
                    options.CaseFilter.UnionWith((Next() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--prices":
                    options.PricesPath = Next();
                    break;
                case "--set":
                    options.SetPath = Next();
                    break;
                case "--out":
                    options.OutPath = Next();
                    break;
                default:
                    options.Errors.Add($"Unknown argument '{name}'.");
                    break;
            }
        }

        return options;
    }

    private static int? PositiveInt(EvalOptions options, string name, string? value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            return number;
        }

        options.Errors.Add($"{name} needs a positive integer.");
        return null;
    }

    private static EvalVariant? Variant(EvalOptions options, string name, string? value)
    {
        if (value is not null && EvalVariant.TryParse(value, out var variant))
        {
            return variant;
        }

        options.Errors.Add($"{name} needs provider:model[:effort], with provider one of {string.Join(", ", AiProviders.All.Select(provider => provider.Value))}.");
        return null;
    }
}
