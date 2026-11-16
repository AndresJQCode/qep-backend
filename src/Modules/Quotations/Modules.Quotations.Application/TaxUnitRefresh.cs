using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Refresca la UVT de un documento editable al guardarlo o previsualizarlo (spec retenciones por UVT §5.4, owner P1): cada
/// fila con base mínima en unidades toma el valor y el año que hoy daría la foto, y el documento se recalcula. Sólo la UVT:
/// nombre, porcentaje y base mínima siguen congelados. No escribe historial propio: es parte del recálculo, como cambiar
/// una cantidad.
///
/// La fecha es perezosa por lo mismo que en <see cref="QuotationWithholdingContext"/>: sin ninguna fila con unidad no se
/// lee ni el calendario del tenant ni Catalog.
/// </summary>
internal static class TaxUnitRefresh
{
    /// <summary>Devuelve si alguna fila cambió de valor o de año.</summary>
    public static async Task<bool> RefreshAsync(
        Quotation quotation,
        IQuotationWithholdingLookup lookup,
        Func<CancellationToken, Task<DateOnly>> documentDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        var units = await UnitsAsync(quotation.Withholdings, lookup, documentDate, cancellationToken);
        if (units is null)
        {
            return false;
        }

        var before = Snapshot(quotation.Withholdings);
        quotation.RefreshTaxUnits(units);
        return !before.SequenceEqual(Snapshot(quotation.Withholdings));
    }

    /// <inheritdoc cref="RefreshAsync(Quotation, IQuotationWithholdingLookup, Func{CancellationToken, Task{DateOnly}}, CancellationToken)"/>
    public static async Task<bool> RefreshAsync(
        Order order,
        IQuotationWithholdingLookup lookup,
        Func<CancellationToken, Task<DateOnly>> documentDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var units = await UnitsAsync(order.Withholdings, lookup, documentDate, cancellationToken);
        if (units is null)
        {
            return false;
        }

        var before = Snapshot(order.Withholdings);
        order.RefreshTaxUnits(units);
        return !before.SequenceEqual(Snapshot(order.Withholdings));
    }

    /// <summary>La fecha de un pedido para la UVT: su <c>CreatedAt</c> en la zona del tenant, la misma con la que se congeló
    /// la foto (<see cref="OrderWithholdingEdits"/>). Perezosa: el calendario se lee sólo si hay algo que refrescar.</summary>
    public static Func<CancellationToken, Task<DateOnly>> DocumentDate(
        ITenantClock tenantClock, Guid tenantId, DateTimeOffset createdAt) =>
        async cancellationToken => DocumentDates.LocalDate(
            await tenantClock.GetAsync(tenantId, cancellationToken), createdAt);

    // Null = ninguna fila con base en unidades: no hay nada que refrescar ni que consultar.
    private static async Task<IReadOnlyDictionary<string, TaxUnitPhoto>?> UnitsAsync(
        IReadOnlyCollection<DocumentWithholding> rows,
        IQuotationWithholdingLookup lookup,
        Func<CancellationToken, Task<DateOnly>> documentDate,
        CancellationToken cancellationToken)
    {
        string[] countries =
        [
            .. rows
                .Where(row => row.MinimumBase is not null && row.MinimumBaseUnit is not null)
                .Select(row => row.Country)
                .Distinct(StringComparer.Ordinal),
        ];
        return countries.Length == 0
            ? null
            : await lookup.TaxUnitsAsync(countries, await documentDate(cancellationToken), cancellationToken);
    }

    // En un orden estable: la colección del agregado no promete ninguno.
    private static (WithholdingSource, short, decimal?, int?)[] Snapshot(IEnumerable<DocumentWithholding> rows) =>
        [
            .. rows
                .OrderBy(row => row.Source)
                .ThenBy(row => row.Position)
                .Select(row => (row.Source, row.Position, row.TaxUnitValue, row.TaxUnitYear)),
        ];
}
