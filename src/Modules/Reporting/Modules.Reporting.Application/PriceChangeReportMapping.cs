using Modules.Reporting.Domain;

namespace Modules.Reporting.Application;

/// <summary>Arma el DTO de una fila del historico, calculando la diferencia. Ver
/// <see cref="IPriceChangeReportSource"/> sobre por que el mapeo vive aca y no en el
/// adaptador.</summary>
public static class PriceChangeReportMapping
{
    public static PriceChangeReportItemDto ToDto(this PriceChangeReportRow row) =>
        new(
            row.ChangeId,
            row.ProductId,
            row.ProductCode,
            row.ProductName,
            row.Field.ToString(),
            // El rango solo tiene sentido para el descuento de una escala. Se recorta aca y no se
            // confia en que el origen lo mande limpio: el contrato dice "non-null ONLY when field
            // is ScaleDiscount", y eso es una afirmacion sobre la respuesta, no sobre la tabla.
            row.Field == PriceChangeField.ScaleDiscount ? row.ScaleFromUnit : null,
            row.Field == PriceChangeField.ScaleDiscount ? row.ScaleToUnit : null,
            row.PreviousValue,
            row.NewValue,
            PriceChangeDifference.Between(row.PreviousValue, row.NewValue),
            row.ChangedById,
            row.ChangedByName,
            row.ChangedAt);

    public static IReadOnlyList<PriceChangeReportItemDto> ToDtos(
        this IReadOnlyList<PriceChangeReportRow> rows) =>
        rows.Select(ToDto).ToArray();
}
