using System.Text.Json;
using Modules.Ai.Application;

namespace Modules.Ai.Eval;

/// <summary>The eval set (<c>eval-set.json</c>): synthetic Spanish conversations with the outcome each one must reach.</summary>
internal sealed class EvalSet
{
    public int Version { get; set; }

    public List<EvalCase> Cases { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public static EvalSet Load(string path) =>
        JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"The eval set at '{path}' is empty.");
}

internal sealed class EvalCase
{
    public string Id { get; set; } = string.Empty;

    /// <summary>One of <see cref="EvalSetValidator.Categories"/>; the report breaks quality down by it.</summary>
    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary><c>known</c>: the conversation has a customer with orders. <c>none</c>: no customer linked yet.</summary>
    public string Customer { get; set; } = EvalSetValidator.KnownCustomer;

    /// <summary>The conversation as the agent reads it. It must end with a customer turn.</summary>
    public List<EvalTurn> History { get; set; } = [];

    public EvalExpectation Expect { get; set; } = new();
}

internal sealed class EvalTurn
{
    /// <summary><c>customer</c>, <c>agent</c> or <c>greeting</c> (the greeting already sent, C-11).</summary>
    public string Role { get; set; } = string.Empty;

    public string? Text { get; set; }

    /// <summary>The <see cref="AgentTurnKind"/> name in lower case (<c>text</c>, <c>image</c>, <c>audio</c>…); text when missing.</summary>
    public string? Kind { get; set; }

    public AgentHistoryTurn ToHistoryTurn() =>
        new(
            Role switch
            {
                "customer" => AgentTurnRole.Customer,
                "agent" => AgentTurnRole.Agent,
                _ => AgentTurnRole.Greeting,
            },
            Text,
            Enum.Parse<AgentTurnKind>(Kind ?? "text", ignoreCase: true));
}

/// <summary>
/// What a case must reach. Every list is optional. The rule checks run first; <see cref="Judge"/> is a rubric for what rules can't
/// check, and only runs when the rules passed and a judge model was configured.
/// </summary>
internal sealed class EvalExpectation
{
    public const string Reply = "reply";
    public const string Handoff = "handoff";
    public const string ReplyOrHandoff = "reply_or_handoff";

    public string Outcome { get; set; } = Reply;

    /// <summary>With a handoff, the accepted reasons (<c>handoff_to_human</c> reasons or a tool handoff code such as
    /// <c>quotation_draft_created</c>). Empty: any reason.</summary>
    public List<string> HandoffReasons { get; set; } = [];

    /// <summary>Tools the agent must have called (all of them).</summary>
    public List<string> RequiredTools { get; set; } = [];

    /// <summary>Tools the agent must not call.</summary>
    public List<string> ForbiddenTools { get; set; } = [];

    /// <summary>With a reply, at least one of these FAQ ids among the cited sources.</summary>
    public List<string> SourcesAny { get; set; } = [];

    /// <summary>With a reply, at least one of these fragments (case, accent and thousands-separator insensitive).</summary>
    public List<string> MustContainAny { get; set; } = [];

    /// <summary>Fragments the reply must never contain, on top of <see cref="EvalFixture.NeverReveal"/>.</summary>
    public List<string> MustNotContain { get; set; } = [];

    /// <summary>The rubric for the judge model, in Spanish. <c>null</c>: the rules are the whole grade.</summary>
    public string? Judge { get; set; }
}
