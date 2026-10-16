using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

// Spec 2026-10-13 §6: contrato con qep-frontend. Los enums viajan con su nombre (Open, Won, Loss…): el diccionario de etiquetas
// lo tiene el frontend.

public sealed record StageDto(Guid Id, string Name, int Position, string Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>BFF: la versión viaja con las etapas porque el <c>PUT</c> la exige; la pantalla de ajustes no la pide aparte.</summary>
public sealed record PipelineStagesDto(Guid PipelineId, long Version, IReadOnlyList<StageDto> Stages);

public sealed record ReasonDto(Guid Id, string Name, int Position);

/// <summary>Sólo los activos, en orden: lo que el modal de pérdida y el de descarte dibujan.</summary>
public sealed record ReasonListDto(string Kind, IReadOnlyList<ReasonDto> Items);

/// <summary>Una etapa del <c>PUT /settings/stages</c>. Todo opcional para que lo que falte salga 422 con su clave en
/// <c>errors</c>, no 400 del binder.</summary>
public sealed record StageRequest(Guid? Id, string? Name, string? Kind, int? Probability, int? StallDays, bool IsQuotationStage);

public sealed record ReasonRequest(Guid? Id, string? Name);

internal static class PipelineMapping
{
    public static PipelineStagesDto ToDto(SalesPipeline pipeline) =>
        new(pipeline.Id, pipeline.Version, [.. pipeline.OrderedStages.Select(ToDto)]);

    public static StageDto ToDto(PipelineStage stage) =>
        new(stage.Id, stage.Name, stage.Position, stage.Kind.ToString(), stage.Probability, stage.StallDays, stage.IsQuotationStage);

    public static ReasonListDto ToDto(ReasonKind kind, IEnumerable<PipelineReason> reasons) =>
        new(kind.ToString(), [.. reasons.Where(reason => reason.IsActive).OrderBy(reason => reason.Position)
            .Select(reason => new ReasonDto(reason.Id, reason.Name, reason.Position))]);
}

public sealed record DealQuotationDto(
    Guid QuotationId, string QuotationNumber, decimal Total, string Currency, string Status, bool IsPrimary, Guid? OrderId, bool OrderCancelled);

/// <summary>Spec 2026-10-15 §7: un pedido sin cotización enlazado al negocio. <c>Source</c> viaja con el nombre
/// (<c>Manual</c>, <c>Shopify</c>…), que el frontend traduce.</summary>
public sealed record DealOrderDto(Guid OrderId, string OrderNumber, decimal Total, string Currency, string Source, bool Cancelled);

/// <summary>D19: una fila del historial con los nombres ya resueltos (una etapa borrada o un miembro que ya no está salen sin
/// nombre o con «Miembro eliminado»).</summary>
public sealed record StageHistoryDto(
    Guid? FromStageId, string? FromStageName, Guid ToStageId, string? ToStageName, DateTimeOffset OccurredAt, string ActorKind,
    Guid? ActorMemberId, string? ActorName, decimal? Amount, string Currency, int Probability);

/// <summary>
/// BFF (§6): toda escritura sobre un negocio devuelve esto entero, y la pantalla lo repinta sin pedir nada más. Viajan resueltos
/// el nombre del cliente y su completitud, el del dueño, el de la etapa, los días en la etapa, el estancado, si el monto se puede
/// editar (D18: no con principal), las cotizaciones enlazadas y las del cliente sin enlazar (para el <c>link</c>), los chats del
/// cliente (D21) y el historial: la pantalla no ve membresías, conversaciones ni etapas por su cuenta.
/// </summary>
public sealed record DealDetailDto(
    Guid Id,
    string Title,
    Guid CustomerId,
    string CustomerName,
    bool CustomerIsComplete,
    Guid OwnerMemberId,
    string OwnerName,
    decimal? Amount,
    string Currency,
    bool AmountIsEditable,
    DateOnly? ExpectedCloseDate,
    Guid StageId,
    string StageName,
    string StageKind,
    int DaysInStage,
    bool IsStalled,
    string Source,
    Guid? OriginConversationId,
    Guid? LossReasonId,
    string? LossReasonName,
    string? LossNote,
    DateTimeOffset? ClosedAt,
    Guid? PrimaryQuotationId,
    IReadOnlyList<DealQuotationDto> Quotations,
    IReadOnlyList<DealQuotationDto> UnlinkedCustomerQuotations,
    IReadOnlyList<Guid> ConversationIds,
    IReadOnlyList<StageHistoryDto> History,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<DealOrderDto> Orders,
    IReadOnlyList<DealOrderDto> UnlinkedCustomerOrders);
