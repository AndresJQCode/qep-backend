using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de cambios de precio. Tercer hermano de los de pedidos y cotizaciones
/// —mismos filtros que el listado menos la paginación, mismo permiso, mismo motivo para existir—
/// con una diferencia que le da la forma entera.
///
/// **Acá no hay ningún agregado de monto, y no es un olvido.** Los valores del histórico conviven
/// en tres unidades dentro de la misma columna: dólares, pesos y puntos de descuento de escala.
/// Sumarlos daría un número sin unidad, y promediarlos también; "el 60 % de los cambios subió el
/// precio" en cambio significa lo mismo sin importar en qué moneda estaba cada fila. Por eso todo
/// lo que se cuenta acá son **filas**, nunca importes, y por eso este resumen no reusa
/// <see cref="ReportMonthlyPointDto"/> ni <see cref="ReportRankEntryDto"/>, que llevan un
/// <c>Totals</c> por moneda que aquí serían siempre mentira o siempre vacíos.
///
/// <see cref="ProductCount"/> son los productos distintos tocados en el periodo — el denominador
/// que le falta a <see cref="ChangeCount"/> para saber si fueron muchos cambios sobre pocos
/// productos o al revés.
/// </summary>
public sealed record PriceChangeReportSummaryDto(
    int ChangeCount,
    int ProductCount,
    int IncreaseCount,
    int DecreaseCount,
    IReadOnlyList<ReportCountPointDto> Monthly,
    IReadOnlyList<PriceChangeFieldSliceDto> ByField,
    IReadOnlyList<PriceChangeProductEntryDto> ByProduct,
    PriceChangeComparisonDto? Previous);

/// <summary>
/// Un mes de la serie con su año y **sólo un conteo**. Ver <see cref="ReportMonthlyPointDto"/>,
/// que es el mismo punto con el monto que este reporte no puede sumar.
///
/// Sólo vienen los meses con cambios: rellenar los huecos con cero depende del rango que el eje
/// dibuje, así que es del frontend.
/// </summary>
public sealed record ReportCountPointDto(int Year, int Month, int Count);

/// <summary>
/// A field — and for PriceBase a currency — with how many times it changed. One PriceBase slice
/// per catalogue currency plus the ScaleDiscount slice, all present even at zero: the catalogue
/// is fixed per deploy, so the screen never has to know which slices exist (same criterion as
/// <see cref="ReportStatusSliceDto"/>).
/// </summary>
public sealed record PriceChangeFieldSliceDto(string Field, string? Currency, int Count);

/// <summary>
/// Un producto del ranking de los más retocados, o la fila "Otros".
///
/// Mismas reglas que <see cref="ReportRankEntryDto"/> —<c>ProductId</c> nulo es el resto plegado,
/// <c>EntityCount</c> dice cuántos productos agrupa— pero sin <c>Totals</c>: ver el encabezado de
/// <see cref="PriceChangeReportSummaryDto"/>.
///
/// **El ranking sigue siendo por producto, con el desglose por variante adentro** (spec 2026-10-20
/// §3.5: el owner pidió las dos cosas). <c>Variants</c> es una colección de tamaño fijo por
/// producto y viaja **completa, incluso en cero**: trae **todas** las variantes del producto,
/// activas e inactivas (las que cuenta <c>VariantCount</c> del catálogo), con <c>Count</c> en cero
/// las que no tuvieron cambios en la ventana filtrada. Un elemento que desaparece obligaría a la
/// pantalla a conocer las variantes del producto para dibujar la que falta («M: 5, L: 7, XL: 0»),
/// y con un filtro activo (<c>variantId</c>, campo o moneda) las demás siguen ahí, en cero, para
/// que el desglose se lea igual con y sin filtro. Los <c>Count</c> suman exactamente el <c>Count</c>
/// del producto. En la fila "Otros" la colección viaja vacía, nunca nula: lo plegado no se
/// desglosa.
///
/// <c>ProductCode</c> es el <c>Code</c> de la única variante del producto, y nulo cuando el
/// producto tiene varias (mismo criterio que el listado del catálogo): el producto ya no tiene un
/// código propio que inventar, y el de cada variante viaja en <c>Variants</c>.
/// </summary>
public sealed record PriceChangeProductEntryDto(
    Guid? ProductId,
    string? ProductName,
    string? ProductCode,
    int EntityCount,
    int Count,
    IReadOnlyList<PriceChangeVariantEntryDto> Variants);

/// <summary>
/// Una variante dentro del desglose de un producto del ranking; <c>Count</c> puede ser cero. <c>Name</c> es el nombre visible
/// (spec §3.4) y <c>Code</c> el de la variante, que es como se reconoce en el resto del reporte.
/// Ordenadas como el producto presenta sus variantes, no por cantidad: así la pantalla repinta
/// M, L, XL en el orden del formulario y el cliente no reordena.
/// </summary>
public sealed record PriceChangeVariantEntryDto(
    Guid VariantId,
    string Name,
    string Code,
    int Count);

/// <summary>
/// El mismo cálculo sobre la ventana anterior, recortado a lo único que un delta puede comparar
/// acá: cuántos cambios hubo. Ver <see cref="ReportComparisonDto"/>, que además lleva el monto.
/// </summary>
public sealed record PriceChangeComparisonDto(int ChangeCount);

/// <summary>Lo que devuelve el origen: el resumen de **una** ventana, sin comparación. Ver
/// <see cref="OrdersReportAggregate"/>.</summary>
public sealed record PriceChangeReportAggregate(
    int ChangeCount,
    int ProductCount,
    int IncreaseCount,
    int DecreaseCount,
    IReadOnlyList<ReportCountPointDto> Monthly,
    IReadOnlyList<PriceChangeFieldSliceDto> ByField,
    IReadOnlyList<PriceChangeProductEntryDto> ByProduct);

/// <summary>El resumen agregado del reporte de cambios de precio. Ver
/// <see cref="GetOrdersReportSummaryQuery"/>.</summary>
public sealed record GetPriceChangeReportSummaryQuery(PriceChangeReportFilter Filter)
    : IQuery<PriceChangeReportSummaryDto>;

/// <summary>
/// A diferencia del de cotizaciones, ningún tramo depende de qué día es hoy: un cambio de precio ya
/// pasó, no vence. El calendario del tenant sólo corta el rango (spec 2026-09-17, punto 4).
/// </summary>
public sealed class GetPriceChangeReportSummaryHandler(
    IPriceChangeReportSource source,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetPriceChangeReportSummaryQuery, PriceChangeReportSummaryDto>
{
    public async Task<PriceChangeReportSummaryDto> HandleAsync(
        GetPriceChangeReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.PriceChangeRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new PriceChangeReportSummaryDto(
            current.ChangeCount,
            current.ProductCount,
            current.IncreaseCount,
            current.DecreaseCount,
            current.Monthly,
            current.ByField,
            current.ByProduct,
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, cancellationToken));
    }

    /// <summary>
    /// El periodo anterior, con los mismos filtros y otra ventana. Ver
    /// <c>GetOrdersReportSummaryHandler.SummarizePrecedingAsync</c>: se copia el criterio entero
    /// cambiando sólo las fechas, para que producto, usuario y campo viajen igual.
    ///
    /// Sin ranking: de la ventana anterior sólo se lee el conteo, y "los productos más retocados
    /// del periodo anterior" no aparece en ninguna pantalla.
    /// </summary>
    private async Task<PriceChangeComparisonDto?> SummarizePrecedingAsync(
        PriceChangeReportFilter filter,
        PriceChangeReportCriteria criteria,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
            rankSize: 0,
            cancellationToken);

        return new PriceChangeComparisonDto(preceding.ChangeCount);
    }
}
