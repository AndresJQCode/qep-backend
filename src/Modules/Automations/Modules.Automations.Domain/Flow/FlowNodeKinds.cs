using System.Collections.Frozen;

namespace Modules.Automations.Domain.Flow;

/// <summary>Spec 2026-10-21 §5.4: los bloques del flujo y las salidas que tiene cada uno.</summary>
public static class FlowNodeKinds
{
    public const string Message = "message";
    public const string OpenQuestion = "openQuestion";
    public const string Buttons = "buttons";
    public const string List = "list";
    public const string Attachment = "attachment";
    public const string Condition = "condition";
    public const string BusinessHours = "businessHours";
    public const string Http = "http";
    public const string Handoff = "handoff";
    public const string Resolve = "resolve";

    public static readonly IReadOnlySet<string> All = new[]
    {
        Message, OpenQuestion, Buttons, List, Attachment, Condition, BusinessHours, Http, Handoff, Resolve,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Las salidas que el motor puede tomar desde el bloque, en el orden en que el editor las pinta. Las de Botones son
    /// los <c>id</c> de sus botones más <c>other</c>; la Lista tiene <c>extra</c> sólo con <c>extraOption</c> y
    /// <c>tooMany</c> sólo si es dinámica (con filas fijas el tope se valida al guardar).
    /// </summary>
    public static IReadOnlyList<string> OutputsOf(FlowNode node) => node switch
    {
        MessageNode => [FlowOutputs.Next],
        BusinessHoursNode => [FlowOutputs.Open, FlowOutputs.Closed],
        ConditionNode => [FlowOutputs.Yes, FlowOutputs.No],
        HttpNode => [FlowOutputs.Ok, FlowOutputs.Error],
        OpenQuestionNode => [FlowOutputs.Answered, FlowOutputs.Invalid],
        ButtonsNode buttons => [.. buttons.Buttons.Select(button => button.Id), FlowOutputs.Other],
        ListNode list => ListOutputs(list),
        AttachmentNode => [FlowOutputs.Received, FlowOutputs.Invalid],
        HandoffNode or ResolveNode => [],
        _ => throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Bloque sin salidas definidas."),
    };

    /// <summary>Pasar a un asesor y Resolver terminan la corrida: no tienen salidas.</summary>
    public static bool IsTerminal(string kind) => kind is Handoff or Resolve;

    private static List<string> ListOutputs(ListNode list)
    {
        var outputs = new List<string> { FlowOutputs.Chosen };
        if (list.Options.ExtraOption is not null)
        {
            outputs.Add(FlowOutputs.Extra);
        }

        if (list.Options.Kind == ListOptionKinds.Dynamic)
        {
            outputs.Add(FlowOutputs.TooMany);
        }

        outputs.Add(FlowOutputs.Other);
        return outputs;
    }
}

/// <summary>Los nombres de salida fijos de §5.4 (los de Botones son los <c>id</c> de cada botón).</summary>
public static class FlowOutputs
{
    public const string Next = "next";
    public const string Answered = "answered";
    public const string Invalid = "invalid";
    public const string Other = "other";
    public const string Chosen = "chosen";
    public const string Extra = "extra";
    public const string TooMany = "tooMany";
    public const string Received = "received";
    public const string Yes = "yes";
    public const string No = "no";
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Ok = "ok";
    public const string Error = "error";
}
