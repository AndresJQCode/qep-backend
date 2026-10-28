namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Limpia un borrador al guardarlo (spec 2026-10-25 D-3/D-4): quita lo que el canvas no dibuja y ya no cuenta. En Botones y Lista, la
/// clave <c>other</c> sólo se queda con <c>fallback = otherOutput</c> y <c>FallbackMemberId</c> sólo con <c>handoff</c>. Los demás
/// bloques salen iguales, salvo Política de datos (spec 2026-10-26 repetir R-1, R-2): <c>rejected</c> sólo se queda con
/// <c>onReject = rejectedOutput</c> y <c>OnRejectMemberId</c> sólo con <c>repeat</c> o <c>handoff</c>; <c>MaxAsks</c> no se toca.
/// </summary>
public static class FlowNormalizer
{
    public static FlowDefinition Normalize(FlowDefinition flow) => flow with { Nodes = [.. flow.Nodes.Select(NormalizeNode)] };

    private static FlowNode NormalizeNode(FlowNode node) => node switch
    {
        ButtonsNode buttons => buttons with
        {
            Outputs = WithoutHidden(buttons.Outputs, FlowOutputs.Other, buttons.Fallback == InteractiveFallbacks.OtherOutput),
            FallbackMemberId = MemberFor(buttons.Fallback, buttons.FallbackMemberId),
        },
        ListNode list => list with
        {
            Outputs = WithoutHidden(list.Outputs, FlowOutputs.Other, list.Fallback == InteractiveFallbacks.OtherOutput),
            FallbackMemberId = MemberFor(list.Fallback, list.FallbackMemberId),
        },
        DataConsentNode consent => consent with
        {
            Outputs = WithoutHidden(consent.Outputs, FlowOutputs.Rejected, consent.OnReject == DataConsentRejectActions.RejectedOutput),
            OnRejectMemberId = consent.OnReject == DataConsentRejectActions.RejectedOutput ? null : consent.OnRejectMemberId,
        },
        _ => node,
    };

    private static IReadOnlyDictionary<string, string?> WithoutHidden(IReadOnlyDictionary<string, string?> outputs, string key, bool keep) =>
        keep
            ? outputs
            : outputs.Where(output => output.Key != key).ToDictionary(output => output.Key, output => output.Value, StringComparer.Ordinal);

    private static Guid? MemberFor(string fallback, Guid? member) => fallback == InteractiveFallbacks.Handoff ? member : null;
}
