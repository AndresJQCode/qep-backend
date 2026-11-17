using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Eval;
using Modules.Ai.Infrastructure;
using Modules.Ai.Infrastructure.Options;

// Plan 2026-11-12, Task 10 (AI-B9): the agent eval. It calls the real providers and spends real money, so it never runs on its own:
// it needs --confirm-spend, a hard cap of model calls and the provider keys in the environment. `--dry-run` checks the set for free.
// How to run it: README, «Agentes IA», «Eval».

const int ExitOk = 0;
const int ExitInvalidSet = 1;
const int ExitRefused = 2;
const int ExitBudgetReached = 3;

var options = EvalOptions.Parse(args);
if (options.Errors.Count > 0)
{
    foreach (var error in options.Errors)
    {
        Console.Error.WriteLine(error);
    }

    Console.Error.WriteLine(EvalOptions.Usage);
    return ExitRefused;
}

var set = EvalSet.Load(options.SetPath ?? Path.Combine(AppContext.BaseDirectory, "eval-set.json"));
var problems = EvalSetValidator.Validate(set);
if (problems.Count > 0)
{
    foreach (var problem in problems)
    {
        Console.Error.WriteLine(problem);
    }

    return ExitInvalidSet;
}

var cases = options.CaseFilter.Count == 0
    ? set.Cases
    : [.. set.Cases.Where(@case => options.CaseFilter.Contains(@case.Id) || options.CaseFilter.Contains(@case.Category))];
Console.WriteLine($"Eval set v{set.Version}: {set.Cases.Count} cases, {cases.Count} selected.");
foreach (var group in cases.GroupBy(@case => @case.Category, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"  {group.Key,-15} {group.Count()}");
}

// Plan fase 2, C3.5: the copilot cases are validated above but not run; their runner (the copilot loop with fixture tools) is pending.
Console.WriteLine($"Copilot cases: {set.CopilotCases.Count} (validated, not run).");

var plannedCalls = cases.Count * options.Repeat * options.Variants.Count;
Console.WriteLine($"Variants: {(options.Variants.Count == 0 ? "none" : string.Join(", ", options.Variants.Select(variant => variant.Label)))}; judge: {options.Judge?.Label ?? "none"}.");
Console.WriteLine($"Turns to run: {plannedCalls} (each one makes 1 to {new AiModelCallOptions().MaxModelCalls} model calls by default, plus one judge call per case with a rubric).");

if (options.DryRun)
{
    Console.WriteLine("Dry run: nothing was sent to any provider.");
    return ExitOk;
}

// The gate. Every refusal names what is missing, never a value.
var refusals = new List<string>();
if (!options.ConfirmSpend)
{
    refusals.Add("Missing --confirm-spend: this run calls real providers and spends money; run it only with the owner's approved budget.");
}

if (options.MaxModelCalls is null)
{
    refusals.Add("Missing --max-model-calls <n>: the hard cap of model calls for the whole run (agent and judge).");
}

if (options.Variants.Count == 0)
{
    refusals.Add("Missing --variant <provider:model[:effort]>.");
}

var providers = options.Variants.Select(variant => variant.Provider)
    .Concat(options.Judge is null ? [] : [options.Judge.Provider])
    .Distinct()
    .ToList();
var keys = new Dictionary<AiProvider, string>();
foreach (var provider in providers)
{
    var name = EvalOptions.KeyVariableOf(provider);
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        refusals.Add($"Missing environment variable {name} with the {provider.Value} API key of the eval account.");
    }
    else
    {
        keys[provider] = value.Trim();
    }
}

if (refusals.Count > 0)
{
    foreach (var refusal in refusals)
    {
        Console.Error.WriteLine(refusal);
    }

    return ExitRefused;
}

// The same wiring production uses for the model adapters (named HttpClients without loggers or redirects, Ai:Model limits): only the
// adapters and the limits are resolved, so the database is never touched and the connection string is a placeholder that nobody opens.
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = "Host=unused-by-the-eval" })
    .Build();
var services = new ServiceCollection();
services.AddLogging(logging => logging.AddSimpleConsole(console => console.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddAiInfrastructure(configuration);
await using var container = services.BuildServiceProvider();
await using var scope = container.CreateAsyncScope();
var limits = scope.ServiceProvider.GetRequiredService<AiRunLimits>();
var loggers = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
var budget = new ModelCallBudget(options.MaxModelCalls!.Value);
var clients = providers.ToDictionary(
    provider => provider,
    provider => (IAiModelClient)new BudgetedModelClient(
        scope.ServiceProvider.GetRequiredKeyedService<IAiModelClient>(provider.Value), budget));
var resolver = new EvalModelClientResolver(clients);
var prices = EvalPrices.Load(options.PricesPath);

// The eval credential: the key of the provider, never printed. The connection id only names it in the runner's log.
static AiCredential CredentialOf(IReadOnlyDictionary<AiProvider, string> keys, AiProvider provider) =>
    new(Guid.Parse("0197a000-0000-7000-8000-00000000e0ff"), keys[provider]);

IEvalJudge? judge = options.Judge is { } judgeVariant
    ? new ModelJudge(clients[judgeVariant.Provider], CredentialOf(keys, judgeVariant.Provider), judgeVariant.Provider, judgeVariant.Model, judgeVariant.Effort)
    : null;

var report = new List<object>();
var exitCode = ExitOk;
foreach (var variant in options.Variants)
{
    var runner = new EvalRunner(resolver, limits, CredentialOf(keys, variant.Provider), judge, loggers);
    var results = new List<EvalCaseResult>();
    try
    {
        foreach (var @case in cases)
        {
            for (var repeat = 1; repeat <= options.Repeat && runner.AccountFailure is null; repeat++)
            {
                var result = await runner.RunAsync(variant, @case, repeat, CancellationToken.None);
                results.Add(result);
                Console.WriteLine($"  {variant.Label} {@case.Id}#{repeat}: {(result.Resolved ? "ok" : "x")} {result.Outcome} {result.LatencyMs} ms");
            }
        }
    }
    catch (EvalBudgetExceededException exception)
    {
        Console.Error.WriteLine(exception.Message);
        exitCode = ExitBudgetReached;
    }

    var summary = EvalReport.Summarize(variant, results, prices, options.Judge, runner.AccountFailure);
    EvalReport.Print(summary, results, Console.Out);
    report.Add(new { summary, results });
    if (exitCode == ExitBudgetReached)
    {
        break;
    }
}

var outPath = options.OutPath
    ?? Path.Combine(Path.GetTempPath(), "qep-ai-eval", string.Create(CultureInfo.InvariantCulture, $"eval-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(
    outPath,
    JsonSerializer.Serialize(
        new { set = set.Version, ranAt = DateTimeOffset.UtcNow, modelCalls = budget.Used, judge = options.Judge?.Label, variants = report },
        EvalOptions.ReportJson));
Console.WriteLine();
Console.WriteLine($"Model calls used: {budget.Used} of {budget.Max}. Report: {outPath}");
return exitCode;
