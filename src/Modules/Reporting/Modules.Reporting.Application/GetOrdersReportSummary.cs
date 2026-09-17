using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen agregado del reporte de pedidos, para el panel.
///
/// No lleva paginación —no hay nada que paginar— pero por lo demás es hermano del listado: mismo
/// filtro, mismo validador, mismo permiso y el mismo orden no negociable de autorizar, validar y
/// recién entonces tocar el origen.
/// </summary>
public sealed record GetOrdersReportSummaryQuery(OrdersReportFilter Filter)
    : IQuery<OrdersReportSummaryDto>;

public sealed class GetOrdersReportSummaryHandler(
    IOrdersReportSource source,
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetOrdersReportSummaryQuery, OrdersReportSummaryDto>
{
    public async Task<OrdersReportSummaryDto> HandleAsync(
        GetOrdersReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.OrdersRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        // El rango y la ventana anterior se cortan con el mismo calendario (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new OrdersReportSummaryDto(
            current.OrderCount,
            current.Subtotal,
            current.TaxAmount,
            current.Total,
            current.Monthly,
            current.ByAdvisor,
            current.ByClient,
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, cancellationToken));
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
        OrdersReportFilter filter,
        OrdersReportCriteria criteria,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        // La ventana se calcula sobre las fechas del filtro y se corta en el día del tenant, igual
        // que la pedida.
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
            ReportSummaryRules.RankSize,
            cancellationToken);

        return new ReportComparisonDto(preceding.OrderCount, preceding.Total);
    }
}
