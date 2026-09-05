using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de cambios de precio. Tercer hermano de los de ventas y cotizaciones
/// —mismos filtros que el listado menos la paginación, mismo permiso, mismo motivo para existir—
/// con una diferencia que le da la forma entera.
///
/// **Acá no hay ningún agregado de monto, y no es un olvido.** Los valores del histórico conviven
/// en tres unidades dentro de la misma columna: dólares, pesos y puntos de descuento de escala.
/// Sumarlos daría un número sin unidad, y promediarlos también; "el 60 % de los cambios subió el
/// precio" en cambio significa lo mismo sin importar en qué moneda estaba cada fila. Por eso todo
/// lo que se cuenta acá son **filas**, nunca importes, y por eso este resumen no reusa
/// <see cref="ReportMonthlyPointDto"/> ni <see cref="ReportRankEntryDto"/>, que llevan un
/// <c>Total</c> que aquí sería siempre mentira o siempre cero.
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
/// Un campo del histórico con cuántas veces se tocó.
///
/// <c>Field</c> viaja como el nombre del enum (<c>PriceBaseUsd</c>), igual que en
/// <see cref="PriceChangeReportItemDto"/>: la traducción es del frontend, que ya tiene el
/// diccionario. **Vienen los tres siempre**, incluso en cero — mismo criterio que
/// <see cref="ReportStatusSliceDto"/>: un campo que desaparece de la respuesta obligaría a la
/// pantalla a saber cuáles existen para dibujar el que falta.
/// </summary>
public sealed record PriceChangeFieldSliceDto(string Field, int Count);

/// <summary>
/// Un producto del ranking de los más retocados, o la fila "Otros".
///
/// Mismas reglas que <see cref="ReportRankEntryDto"/> —<c>ProductId</c> nulo es el resto plegado,
/// <c>EntityCount</c> dice cuántos productos agrupa— pero sin <c>Total</c>: ver el encabezado de
/// <see cref="PriceChangeReportSummaryDto"/>. El código viaja además del nombre porque es como se
/// identifica un producto en el resto del reporte.
/// </summary>
public sealed record PriceChangeProductEntryDto(
    Guid? ProductId,
    string? ProductName,
    string? ProductCode,
    int EntityCount,
    int Count);

/// <summary>
/// El mismo cálculo sobre la ventana anterior, recortado a lo único que un delta puede comparar
/// acá: cuántos cambios hubo. Ver <see cref="ReportComparisonDto"/>, que además lleva el monto.
/// </summary>
public sealed record PriceChangeComparisonDto(int ChangeCount);

/// <summary>Lo que devuelve el origen: el resumen de **una** ventana, sin comparación. Ver
/// <see cref="SalesReportAggregate"/>.</summary>
public sealed record PriceChangeReportAggregate(
    int ChangeCount,
    int ProductCount,
    int IncreaseCount,
    int DecreaseCount,
    IReadOnlyList<ReportCountPointDto> Monthly,
    IReadOnlyList<PriceChangeFieldSliceDto> ByField,
    IReadOnlyList<PriceChangeProductEntryDto> ByProduct);

/// <summary>El resumen agregado del reporte de cambios de precio. Ver
/// <see cref="GetSalesReportSummaryQuery"/>.</summary>
public sealed record GetPriceChangeReportSummaryQuery(PriceChangeReportFilter Filter)
    : IQuery<PriceChangeReportSummaryDto>;

/// <summary>
/// Sin reloj inyectado, a diferencia del de cotizaciones: no hay ningún tramo que dependa de qué
/// día es hoy. Un cambio de precio ya pasó — no vence.
/// </summary>
public sealed class GetPriceChangeReportSummaryHandler(
    IPriceChangeReportSource source,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext)
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

        var criteria = query.Filter.ToCriteria();
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
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }

    /// <summary>
    /// El periodo anterior, con los mismos filtros y otra ventana. Ver
    /// <c>GetSalesReportSummaryHandler.SummarizePrecedingAsync</c>: se copia el criterio entero
    /// cambiando sólo las fechas, para que producto, usuario y campo viajen igual.
    ///
    /// Sin ranking: de la ventana anterior sólo se lee el conteo, y "los productos más retocados
    /// del periodo anterior" no aparece en ninguna pantalla.
    /// </summary>
    private async Task<PriceChangeComparisonDto?> SummarizePrecedingAsync(
        PriceChangeReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
            rankSize: 0,
            cancellationToken);

        return new PriceChangeComparisonDto(preceding.ChangeCount);
    }
}
