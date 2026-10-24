using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Lo que se dice igual de un negocio en el tablero, el detalle y la ficha del cliente.</summary>
internal sealed record DealCardFacts(string StageName, StageKind StageKind, string OwnerName, int DaysInStage, bool IsStalled);

/// <summary>
/// Spec 2026-10-13 §5.2, D22 y §6: el único lugar que resuelve la tarjeta de un negocio —nombre y tipo de la etapa, nombre del
/// dueño (o «Miembro eliminado»), días en la etapa y estancado—. Lo usan el detalle (<see cref="DealDetailBuilder"/>), el tablero
/// (<c>GetBoardHandler</c>) y la ficha del cliente (<c>GetCustomerDealsSummaryHandler</c>): una regla que cambia, cambia para las
/// tres pantallas a la vez. Recibe columnas sueltas y no un <see cref="Deal"/> porque el tablero lee filas de su consulta.
/// </summary>
internal static class DealCardMapper
{
    public const string DeletedMemberName = "Miembro eliminado";
    public const string UnknownCustomerName = "Cliente no disponible";

    /// <param name="lastMessageAt">El último mensaje de las conversaciones del cliente, entrante o saliente (§5.2), si hay.</param>
    public static DealCardFacts Describe(
        SalesPipeline pipeline, Guid stageId, DateTimeOffset stageEnteredAt, DateTimeOffset lastChangedAt, Guid ownerMemberId,
        IReadOnlyDictionary<Guid, string> memberNames, DateTimeOffset? lastMessageAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(memberNames);
        // Defensivo: la FK con RESTRICT no deja borrar la etapa de un negocio. Si faltara, sin nombre y nunca estancado.
        var stage = pipeline.FindStage(stageId);
        return new DealCardFacts(
            stage?.Name ?? string.Empty,
            stage?.Kind ?? StageKind.Open,
            MemberName(memberNames, ownerMemberId),
            StallRules.DaysIn(stageEnteredAt, now),
            IsStalled(stage, lastChangedAt, lastMessageAt, now));
    }

    /// <summary>El estancado de <see cref="Describe"/> sin el resto de la tarjeta: la franja del tablero lo cuenta sobre todos los
    /// negocios abiertos, también los que no traen tarjeta, y tiene que decir exactamente lo mismo que cada tarjeta.</summary>
    public static bool IsStalled(SalesPipeline pipeline, Guid stageId, DateTimeOffset lastChangedAt, DateTimeOffset? lastMessageAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return IsStalled(pipeline.FindStage(stageId), lastChangedAt, lastMessageAt, now);
    }

    private static bool IsStalled(PipelineStage? stage, DateTimeOffset lastChangedAt, DateTimeOffset? lastMessageAt, DateTimeOffset now) =>
        stage is not null && StallRules.IsStalled(stage.Kind, stage.StallDays, StallRules.LastActivity(lastChangedAt, lastMessageAt), now);

    /// <summary>Una membresía que ya no está en el tenant no aparece en el diccionario: la pantalla nunca recibe <c>null</c>.</summary>
    public static string MemberName(IReadOnlyDictionary<Guid, string> memberNames, Guid memberId)
    {
        ArgumentNullException.ThrowIfNull(memberNames);
        return memberNames.GetValueOrDefault(memberId) ?? DeletedMemberName;
    }

    public static string CustomerName(PipelineCustomerRef? customer) => customer?.Name ?? UnknownCustomerName;
}
