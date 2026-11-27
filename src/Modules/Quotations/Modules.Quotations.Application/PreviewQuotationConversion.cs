using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Lo que la conversión de una cotización daría hoy, sin convertir (spec alerta UVT §5.3): el pedido nace con la UVT de su
/// año (§5.2), y el asistente de conversión compara los comprobantes contra ese neto, no contra el de la cotización. No
/// escribe, no toma el contador y no copia comprobantes.
/// </summary>
public sealed record PreviewQuotationConversionQuery(Guid TenantId, Guid QuotationId) : IQuery<QuotationConversionPreviewDto>;

/// <summary>Los totales y el desglose del pedido que nacería, más qué cambió por la UVT.</summary>
/// <param name="CreatedAt">El instante con que se armó el pedido: el compositor lo usa como fecha del documento para
/// <c>taxUnitOutdated</c> y <c>taxUnitLatestYear</c>, igual que haría con el pedido ya creado.</param>
/// <param name="TaxUnitChange">Null cuando ninguna fila cambió de valor ni de año de UVT.</param>
public sealed record QuotationConversionPreviewDto(
    decimal RetentionAmount,
    decimal NetTotal,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WithholdingVerdictDto> WithholdingBreakdown,
    TaxUnitChangeDto? TaxUnitChange);

/// <summary>
/// La diferencia entre la foto de la cotización y el pedido con la UVT del día. Las dos mitades las calcula el backend,
/// con el mismo dominio que la conversión: el frontend no resta ni compara montos (spec alerta UVT §5.3).
/// </summary>
/// <param name="TaxUnits">Un elemento por país cuya UVT cambió de valor o de año.</param>
/// <param name="Withholdings">Sólo las filas cuyo monto o veredicto cambió, en el orden del desglose. Puede quedar vacía
/// con <paramref name="TaxUnits"/> lleno: la base está lejos del umbral y la pantalla no muestra alerta.</param>
public sealed record TaxUnitChangeDto(
    IReadOnlyList<TaxUnitShiftDto> TaxUnits,
    IReadOnlyList<WithholdingShiftDto> Withholdings,
    decimal FromNetTotal,
    decimal ToNetTotal);

/// <param name="FromYear">Null en una fila anterior a la UVT con año (spec retenciones por UVT §8.2).</param>
/// <param name="FromValue">Null si la cotización no tenía ningún valor cargado para el país.</param>
public sealed record TaxUnitShiftDto(string Country, int? FromYear, decimal? FromValue, int ToYear, decimal ToValue);

public sealed record WithholdingShiftDto(
    Guid TaxRateId,
    string Name,
    string Type,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? FromMinimumBaseAmount,
    decimal? ToMinimumBaseAmount,
    decimal Base,
    bool FromApplies,
    bool ToApplies,
    decimal FromAmount,
    decimal ToAmount);

/// <summary>
/// Lo que viaja al asistente de conversión: el neto con que compara los comprobantes, el desglose con la misma forma que en
/// el detalle (con <c>taxUnitLatestYear</c>, spec alerta UVT §5.4) y el cambio de UVT, si hubo.
/// </summary>
public sealed record QuotationConversionPreviewResponse(
    decimal RetentionAmount,
    decimal NetTotal,
    IReadOnlyList<WithholdingBreakdownResponse> WithholdingBreakdown,
    TaxUnitChangeDto? TaxUnitChange);

public sealed class PreviewQuotationConversionHandler(
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IQuotationWithholdingLookup withholdingLookup,
    ITenantModules tenantModules)
    : IQueryHandler<PreviewQuotationConversionQuery, QuotationConversionPreviewDto>
{
    // El pedido de la vista previa nunca se guarda ni sale en la respuesta: el número sólo tiene que no estar vacío, y no
    // sale del contador, que no tiene huecos (ver ConvertQuotationToOrderHandler).
    private const string PreviewOrderNumber = "PREVIEW";

    public async Task<QuotationConversionPreviewDto> HandleAsync(
        PreviewQuotationConversionQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, OrdersPermissions.OrderManage);

        var quotation = await quotationRepository.FindAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(query.QuotationId);
        await ProjectModuleGuard.EnsureAsync(
            tenantModules, query.TenantId, quotation.Kind, cancellationToken);

        // Las mismas precondiciones que la conversión revisa antes de pedir el número. La elegibilidad del cliente y el
        // tipo de persona no (spec alerta UVT §5.3): son errores del POST, y el asistente ya los muestra ahí.
        quotation.EnsureConvertibleToOrder();

        // La misma fecha y la misma lectura que ConvertQuotationToOrderHandler (spec alerta UVT §5.1): lo que se muestra y
        // lo que se guarda no pueden divergir.
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var taxUnits = await TaxUnitRefresh.UnitsForAsync(
            quotation.ActiveWithholdings, withholdingLookup, DocumentDates.LocalDate(calendar, now), cancellationToken);

        // «Antes» es el pedido con la foto de la cotización; «después», el mismo pedido con la UVT del día. Ninguno se
        // agrega al repositorio: la consulta no tiene unidad de trabajo y nada se guarda.
        // Sin filas en unidades (taxUnits null) los dos serían el mismo pedido: no se arma el segundo.
        // El mismo día local que ConvertQuotationToOrderHandler le pone al pedido (spec 2026-11-16 §3).
        var orderDate = DocumentDates.LocalDate(calendar, now);
        var after = PreviewOrder(query.TenantId, quotation, now, orderDate, taxUnits).ToDto();
        TaxUnitChangeDto? change = null;
        if (taxUnits is not null)
        {
            var before = PreviewOrder(query.TenantId, quotation, now, orderDate, taxUnits: null).ToDto();
            change = QuotationConversionPreview.Compare(
                before.WithholdingBreakdown ?? [], before.NetTotal, after.WithholdingBreakdown ?? [], after.NetTotal);
        }

        return new QuotationConversionPreviewDto(
            after.RetentionAmount, after.NetTotal, now, after.WithholdingBreakdown ?? [], change);
    }

    private static Order PreviewOrder(
        Guid tenantId,
        Quotation quotation,
        DateTimeOffset now,
        DateOnly orderDate,
        IReadOnlyDictionary<string, TaxUnitPhoto>? taxUnits) =>
        Order.FromQuotation(
            OrderId.New(), tenantId, PreviewOrderNumber, quotation, notes: null, quotation.AdvisorId, [], now, orderDate,
            taxUnits);
}

internal static class QuotationConversionPreview
{
    /// <summary>
    /// Qué cambió entre el desglose de la foto y el refrescado. Hay cambio de UVT si alguna fila con base en unidades difiere
    /// en valor o año; si no, null. Las filas se emparejan por posición: los dos pedidos salen de la misma cotización, con
    /// las mismas tasas en el mismo orden.
    /// </summary>
    internal static TaxUnitChangeDto? Compare(
        IReadOnlyList<WithholdingVerdictDto> before,
        decimal beforeNet,
        IReadOnlyList<WithholdingVerdictDto> after,
        decimal afterNet)
    {
        // Zip corta en silencio al largo menor: si los desgloses no casan fila por fila, la comparación saldría incompleta o
        // mezclaría una tasa con otra sin que nada lo note. No pasa con dos pedidos de la misma cotización (spec alerta UVT
        // §5.3), así que es un error de programación y no un código de dominio.
        if (before.Count != after.Count)
        {
            throw new InvalidOperationException(
                $"The conversion preview breakdowns differ in length ({before.Count} before, {after.Count} after).");
        }

        var pairs = before.Zip(after).ToArray();
        foreach (var (first, second) in pairs)
        {
            if (first.TaxRateId != second.TaxRateId)
            {
                throw new InvalidOperationException(
                    $"The conversion preview paired tax rate {first.TaxRateId} with {second.TaxRateId}.");
            }
        }

        TaxUnitShiftDto[] taxUnits =
        [
            .. pairs
                // Una fila que pasa de tener UVT a no tenerla (su país ya no está en TaxUnitsAsync) no se reporta, y por
                // eso ToYear y ToValue del contrato no son nulos (spec alerta UVT §5.3). Supuesto: no hay ruta para borrar
                // valores de UVT, así que un refresco nunca deja sin valor a una fila que lo tenía; sólo podría pasarle a
                // una fila legada de un país sin ningún valor cargado, y esa tampoco tenía valor antes. Si algún día se
                // pueden borrar valores, este filtro esconde un cambio del neto y hay que revisarlo.
                .Where(pair => pair.Second.MinimumBaseUnit is not null
                    && pair.Second.TaxUnitValue is not null
                    && pair.Second.TaxUnitYear is not null
                    && (pair.First.TaxUnitValue, pair.First.TaxUnitYear) != (pair.Second.TaxUnitValue, pair.Second.TaxUnitYear))
                .DistinctBy(pair => pair.Second.Country, StringComparer.Ordinal)
                .Select(pair => new TaxUnitShiftDto(
                    pair.Second.Country,
                    pair.First.TaxUnitYear,
                    pair.First.TaxUnitValue,
                    pair.Second.TaxUnitYear!.Value,
                    pair.Second.TaxUnitValue!.Value)),
        ];
        if (taxUnits.Length == 0)
        {
            return null;
        }

        WithholdingShiftDto[] withholdings =
        [
            .. pairs
                .Where(pair => pair.First.Applies != pair.Second.Applies || pair.First.Amount != pair.Second.Amount)
                .Select(pair => new WithholdingShiftDto(
                    pair.Second.TaxRateId,
                    pair.Second.Name,
                    pair.Second.Type,
                    pair.Second.MinimumBase,
                    pair.Second.MinimumBaseUnit,
                    pair.First.MinimumBaseAmount,
                    pair.Second.MinimumBaseAmount,
                    pair.Second.Base,
                    pair.First.Applies,
                    pair.Second.Applies,
                    pair.First.Amount,
                    pair.Second.Amount)),
        ];
        return new TaxUnitChangeDto(taxUnits, withholdings, beforeNet, afterNet);
    }
}
