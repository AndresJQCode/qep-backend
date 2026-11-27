using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Domain.Control;

/// <summary>Sobre qué trata una novedad: la tarea y la línea de donde nace (si nace de una; se guardan como <c>Guid</c>, sin FK) y la
/// clave de stock y las cantidades en juego. Todo es opcional: una etiqueta ilegible sólo trae la ubicación.</summary>
public sealed record IncidentSubject(
    Guid? SourceTaskId,
    Guid? SourceTaskLineId,
    LocationId? Location,
    Guid? VariantId,
    LotId? LotId,
    StockStatus? StockStatus,
    decimal? ExpectedQty,
    decimal? ActualQty)
{
    public static IncidentSubject Empty { get; } = new(null, null, null, null, null, null, null, null);
}
