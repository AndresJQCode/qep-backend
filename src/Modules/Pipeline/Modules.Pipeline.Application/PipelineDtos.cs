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
