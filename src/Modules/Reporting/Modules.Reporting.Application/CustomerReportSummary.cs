using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de clientes. Cuarto hermano de los de ventas, cotizaciones y cambios de
/// precio —mismos filtros que el listado menos la paginación, mismo permiso, mismo motivo para
/// existir— y el que cierra el módulo: hasta acá clientes era el único que seguía siendo una tabla.
///
/// **Acá no hay ningún agregado de monto, y tampoco es un olvido.** Un cliente no factura ni vence:
/// no tiene un número que sumar. Lo que sí se puede medir sin inventar nada es cuántos son, cuántos
/// están activos, cuándo entraron y cómo se reparten por clasificación y por geografía. Por eso
/// este resumen reusa <see cref="ReportCountPointDto"/> —el punto mensual sin monto que estrenó el
/// de cambios de precio— y no <see cref="ReportMonthlyPointDto"/>, que lleva un <c>Total</c> que
/// aquí sería siempre cero.
///
/// **Los inactivos no viajan: son la resta.** <c>CustomerCount - ActiveCount</c>, y un campo más
/// es un campo más que puede desincronizarse de los dos que lo definen — mismo criterio que el
/// ticket promedio de ventas, que el panel deriva. Se puede porque el reparto es binario y
/// exhaustivo, a diferencia de la dirección de un cambio de precio: ahí hay un tercer grupo —los
/// que no se movieron— y por eso ese resumen sí manda las dos puntas.
/// </summary>
public sealed record CustomerReportSummaryDto(
    int CustomerCount,
    int ActiveCount,
    IReadOnlyList<ReportCountPointDto> Monthly,
    IReadOnlyList<CustomerGroupEntryDto> ByClassification,
    IReadOnlyList<CustomerGroupEntryDto> ByDepartment,
    CustomerComparisonDto? Previous);

/// <summary>
/// Un grupo del reparto de la cartera —una clasificación o un departamento—, o la fila "Otros".
///
/// Mismas reglas que <see cref="ReportRankEntryDto"/>: <c>Id</c> nulo es el resto plegado, y
/// <c>EntityCount</c> dice cuántas entidades agrupa (1 en una fila normal, el resto en la de
/// "Otros") para que el frontend escriba "Otros (7)" sin adivinar. Sin <c>Total</c>, por lo que
/// dice el encabezado de <see cref="CustomerReportSummaryDto"/>.
///
/// <c>Label</c> nulo en una fila con <c>Id</c> es una clasificación que ya no existe: el join es
/// izquierdo a propósito —entre <c>customers</c> y <c>client_classifications</c> no hay FK real—
/// para que un cliente no desaparezca del reporte porque le borraron la clasificación.
/// </summary>
public sealed record CustomerGroupEntryDto(
    Guid? Id,
    string? Label,
    int EntityCount,
    int Count);

/// <summary>
/// El mismo cálculo sobre la ventana anterior, recortado a lo único que un delta puede comparar
/// acá: cuántos clientes entraron. Ver <see cref="ReportComparisonDto"/>, que además lleva el
/// monto.
/// </summary>
public sealed record CustomerComparisonDto(int CustomerCount);

/// <summary>Lo que devuelve el origen: el resumen de **una** ventana, sin comparación. Ver
/// <see cref="SalesReportAggregate"/>.</summary>
public sealed record CustomerReportAggregate(
    int CustomerCount,
    int ActiveCount,
    IReadOnlyList<ReportCountPointDto> Monthly,
    IReadOnlyList<CustomerGroupEntryDto> ByClassification,
    IReadOnlyList<CustomerGroupEntryDto> ByDepartment);

/// <summary>El resumen agregado del reporte de clientes. Ver
/// <see cref="GetSalesReportSummaryQuery"/>.</summary>
public sealed record GetCustomerReportSummaryQuery(CustomerReportFilter Filter)
    : IQuery<CustomerReportSummaryDto>;

/// <summary>
/// Sin reloj inyectado, igual que el de cambios de precio: no hay ningún tramo que dependa de qué
/// día es hoy. Un alta ya pasó — no vence.
/// </summary>
public sealed class GetCustomerReportSummaryHandler(
    ICustomerReportSource source,
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<GetCustomerReportSummaryQuery, CustomerReportSummaryDto>
{
    public async Task<CustomerReportSummaryDto> HandleAsync(
        GetCustomerReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.CustomerRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var criteria = query.Filter.ToCriteria();
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new CustomerReportSummaryDto(
            current.CustomerCount,
            current.ActiveCount,
            current.Monthly,
            current.ByClassification,
            current.ByDepartment,
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }

    /// <summary>
    /// El periodo anterior, con los mismos filtros y otra ventana. Ver
    /// <c>GetSalesReportSummaryHandler.SummarizePrecedingAsync</c>: se copia el criterio entero
    /// cambiando sólo las fechas, para que estado, clasificación y departamento viajen igual.
    ///
    /// Sin rankings: de la ventana anterior sólo se lee el conteo, y "los departamentos del periodo
    /// anterior" no aparece en ninguna pantalla.
    /// </summary>
    private async Task<CustomerComparisonDto?> SummarizePrecedingAsync(
        CustomerReportCriteria criteria,
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

        return new CustomerComparisonDto(preceding.CustomerCount);
    }
}
