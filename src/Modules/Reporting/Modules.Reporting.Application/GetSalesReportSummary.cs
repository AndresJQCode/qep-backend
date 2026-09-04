using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen agregado del reporte de ventas, para el panel.
///
/// No lleva paginación —no hay nada que paginar— pero por lo demás es hermano del listado: mismo
/// filtro, mismo validador, mismo permiso y el mismo orden no negociable de autorizar, validar y
/// recién entonces tocar el origen.
/// </summary>
public sealed record GetSalesReportSummaryQuery(SalesReportFilter Filter)
    : IQuery<SalesReportSummaryDto>;

public sealed class GetSalesReportSummaryHandler(
    ISalesReportSource source,
    IValidator<SalesReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<GetSalesReportSummaryQuery, SalesReportSummaryDto>
{
    public async Task<SalesReportSummaryDto> HandleAsync(
        GetSalesReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.SalesRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var criteria = query.Filter.ToCriteria();
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new SalesReportSummaryDto(
            current.SaleCount,
            current.Subtotal,
            current.TaxAmount,
            current.Total,
            current.Monthly,
            current.ByAdvisor,
            current.ByClient,
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }

    /// <summary>
    /// El periodo anterior es **una segunda consulta con los mismos filtros y otra ventana**, no
    /// una resta sobre lo ya traído: el agregado del periodo pedido no contiene nada de antes.
    ///
    /// Se copia el criterio entero cambiando sólo las fechas, para que asesor, cliente y estado
    /// de pago viajen igual. Comparar "enero del asesor X" contra "diciembre de todos" sería un
    /// delta inventado, y es exactamente el error que se comete armando el segundo criterio a
    /// mano campo por campo.
    /// </summary>
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        SalesReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
            ReportSummaryRules.RankSize,
            cancellationToken);

        return new ReportComparisonDto(preceding.SaleCount, preceding.Total);
    }
}
