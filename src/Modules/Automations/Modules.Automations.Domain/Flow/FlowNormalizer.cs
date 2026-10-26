namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Limpia un borrador al guardarlo (spec 2026-10-25 D-3/D-4): quita lo que el canvas no dibuja y ya no cuenta. En Botones y Lista, la
/// clave <c>other</c> sólo se queda con <c>fallback = otherOutput</c> y <c>FallbackMemberId</c> sólo con <c>handoff</c>. Los demás
/// bloques salen iguales.
/// </summary>
public static class FlowNormalizer
{
    public static FlowDefinition Normalize(FlowDefinition flow) => flow with { Nodes = [.. flow.Nodes.Select(NormalizeNode)] };

    private static FlowNode NormalizeNode(FlowNode node) => node switch
    {
        ButtonsNode buttons => buttons with
        {
            Outputs = WithoutHiddenOther(buttons.Outputs, buttons.Fallback),
            FallbackMemberId = MemberFor(buttons.Fallback, buttons.FallbackMemberId),
        },
        ListNode list => list with
        {
            Outputs = WithoutHiddenOther(list.Outputs, list.Fallback),
            FallbackMemberId = MemberFor(list.Fallback, list.FallbackMemberId),
        },
        _ => node,
    };

    private static IReadOnlyDictionary<string, string?> WithoutHiddenOther(IReadOnlyDictionary<string, string?> outputs, string fallback) =>
        fallback == InteractiveFallbacks.OtherOutput
            ? outputs
            : outputs.Where(output => output.Key != FlowOutputs.Other).ToDictionary(output => output.Key, output => output.Value, StringComparer.Ordinal);

    private static Guid? MemberFor(string fallback, Guid? member) => fallback == InteractiveFallbacks.Handoff ? member : null;
}
