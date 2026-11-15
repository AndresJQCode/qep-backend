using Modules.Ai.Application;

namespace Modules.Ai.Eval;

/// <summary>Checks the eval set before anything spends money: a broken case would burn a model call and grade nothing.</summary>
internal static class EvalSetValidator
{
    public const string KnownCustomer = "known";
    public const string NoCustomer = "none";

    public const int MinCases = 30;
    public const int MaxCases = 50;

    public static readonly IReadOnlyList<string> Categories =
    [
        "faq", "catalog", "order_status", "other_customer", "quotation", "human_request", "complaint", "out_of_scope", "injection",
        "burst", "image", "continuity", "no_answer", "privacy",
    ];

    /// <summary>The categories the plan requires (Task 10): each one needs at least a case.</summary>
    public static readonly IReadOnlyList<string> RequiredCategories =
        ["out_of_scope", "human_request", "injection", "other_customer", "burst", "image"];

    private static readonly IReadOnlyList<string> Tools =
    [
        AgentToolNames.SearchCatalog, AgentToolNames.GetOrderStatus, AgentToolNames.CreateQuotationDraft,
    ];

    private static readonly IReadOnlyList<string> Roles = ["customer", "agent", "greeting"];

    public static IReadOnlyList<string> Validate(EvalSet set)
    {
        var errors = new List<string>();
        if (set.Cases.Count is < MinCases or > MaxCases)
        {
            errors.Add($"The set has {set.Cases.Count} cases; the plan asks for {MinCases} to {MaxCases}.");
        }

        foreach (var duplicate in set.Cases.GroupBy(@case => @case.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"Case id '{duplicate.Key}' is repeated.");
        }

        foreach (var missing in RequiredCategories.Where(category => set.Cases.All(@case => @case.Category != category)))
        {
            errors.Add($"No case covers the required category '{missing}'.");
        }

        var handoffReasons = AgentHandoffReasons.All.Append(AgentHandoffCodes.QuotationDraftCreated).ToHashSet(StringComparer.Ordinal);
        foreach (var @case in set.Cases)
        {
            var at = $"Case '{@case.Id}'";
            if (string.IsNullOrWhiteSpace(@case.Id))
            {
                errors.Add("A case has no id.");
            }

            if (!Categories.Contains(@case.Category))
            {
                errors.Add($"{at}: unknown category '{@case.Category}'.");
            }

            if (@case.Customer is not (KnownCustomer or NoCustomer))
            {
                errors.Add($"{at}: customer must be '{KnownCustomer}' or '{NoCustomer}'.");
            }

            if (@case.History.Count == 0 || @case.History[^1].Role != "customer")
            {
                errors.Add($"{at}: the history must end with a customer turn.");
            }

            foreach (var turn in @case.History)
            {
                if (!Roles.Contains(turn.Role))
                {
                    errors.Add($"{at}: unknown role '{turn.Role}'.");
                }

                if (!Enum.TryParse<AgentTurnKind>(turn.Kind ?? "text", ignoreCase: true, out var kind))
                {
                    errors.Add($"{at}: unknown kind '{turn.Kind}'.");
                }
                else if (kind == AgentTurnKind.Text && string.IsNullOrWhiteSpace(turn.Text))
                {
                    errors.Add($"{at}: a text turn needs text.");
                }
            }

            var expect = @case.Expect;
            if (expect.Outcome is not (EvalExpectation.Reply or EvalExpectation.Handoff or EvalExpectation.ReplyOrHandoff))
            {
                errors.Add($"{at}: unknown outcome '{expect.Outcome}'.");
            }

            errors.AddRange(expect.HandoffReasons.Where(reason => !handoffReasons.Contains(reason))
                .Select(reason => $"{at}: unknown handoff reason '{reason}'."));
            errors.AddRange(expect.RequiredTools.Concat(expect.ForbiddenTools).Where(tool => !Tools.Contains(tool))
                .Select(tool => $"{at}: unknown tool '{tool}'."));
            errors.AddRange(expect.SourcesAny.Where(source => EvalFixture.Faq.All(faq => faq.Id != source))
                .Select(source => $"{at}: unknown FAQ id '{source}'."));
        }

        return errors;
    }
}
