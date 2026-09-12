using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El panel del listado de ventas: cuánto se vendió en el período, cómo se reparte entre lo que
/// espera visto bueno y lo aprobado, y cuánto de eso está efectivamente cobrado.
///
/// Lleva sólo la ventana de fechas y **no** el resto de los filtros del listado, a diferencia del
/// resumen del reporte de ventas: son cuatro cifras de contexto —"el mes"— y no el resumen de lo
/// que la grilla está mostrando. Filtrar por asesora abajo no tiene que mover el encabezado.
///
/// La ventana la manda el cliente en vez de calcularla acá con el reloj: "este mes" empieza en la
/// medianoche de quien mira, y el servidor no conoce su huso.
/// </summary>
public sealed record GetSalesSummaryQuery(Guid TenantId, DateOnly From, DateOnly To)
    : IQuery<SaleSummaryDto>;

/// <summary>
/// <para>
/// Los importes se suman **sin separar por moneda**, mismo criterio que el resumen del reporte de
/// ventas (<c>GetSalesReportSummaryHandler</c>): una venta hereda la moneda de su cotización y
/// casi todas son COP. Si un tenant llegara a mezclar en serio, hay que partirlo en los dos
/// lugares a la vez — no en uno solo, que dejaría dos paneles diciendo cosas distintas.
/// </para>
/// <para>
/// <c>SaleCount</c> y <c>Total</c> no son campos aparte en la base: son la suma de los dos
/// estados. Viajan resueltos porque el encabezado los muestra como una cifra propia.
/// </para>
/// </summary>
public sealed record SaleSummaryDto(
    int SaleCount,
    decimal Total,
    int PendingCount,
    decimal PendingTotal,
    int ApprovedCount,
    decimal ApprovedTotal,
    /// <summary>Lo cargado en comprobantes sobre las ventas del período. No es "lo que entró
    /// este mes": un comprobante se adjunta a la venta, no a una fecha de caja.</summary>
    decimal CollectedTotal,
    /// <summary>El total del período inmediatamente anterior y de la misma longitud, para la
    /// variación. Nunca nulo: toda ventana tiene una anterior.</summary>
    decimal PreviousTotal);

public sealed class GetSalesSummaryHandler(
    ISaleRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetSalesSummaryQuery, SaleSummaryDto>
{
    public async Task<SaleSummaryDto> HandleAsync(
        GetSalesSummaryQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SalesPermissions.SaleRead);

        if (query.To < query.From)
        {
            throw new QuotationsDomainException(
                "sale.summary.range_invalid",
                "The end of the range cannot be earlier than its start.");
        }

        var current = await repository.SummarizeAsync(
            query.TenantId, query.From, query.To, cancellationToken);

        // El período anterior es otra consulta con la misma longitud pegada antes, no una resta
        // sobre lo ya traído: el agregado del período pedido no contiene nada de antes. Mismo
        // criterio que `GetSalesReportSummaryHandler.SummarizePrecedingAsync`.
        var length = query.To.DayNumber - query.From.DayNumber + 1;
        var previous = await repository.SummarizeAsync(
            query.TenantId,
            query.From.AddDays(-length),
            query.From.AddDays(-1),
            cancellationToken);

        return new SaleSummaryDto(
            current.PendingCount + current.ApprovedCount,
            current.PendingTotal + current.ApprovedTotal,
            current.PendingCount,
            current.PendingTotal,
            current.ApprovedCount,
            current.ApprovedTotal,
            current.CollectedTotal,
            previous.PendingTotal + previous.ApprovedTotal);
    }
}
