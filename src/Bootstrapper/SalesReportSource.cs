using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta <c>quotations</c>, <c>customers</c>, <c>tenancy</c> e <c>identity</c> al puerto que
/// declara <c>reporting</c>.
///
/// **Vive aca y no en ninguno de esos modulos**, mismo criterio que <c>ProductImageLookup</c>
/// (CAT-05) y <c>QuotationCustomerLookup</c>: ningun modulo de negocio referencia al otro
/// —<c>ReportingLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules</c> lo impide
/// a proposito— y el composition root, que ya los referencia a todos, es el unico lugar donde
/// ese acoplamiento es legitimo. Reporting es un caso extremo del patron: es lectura pura sobre
/// datos ajenos, asi que **todos** sus origenes cruzan la frontera.
///
/// **No decide nada.** Los limites de la exportacion, la normalizacion de la paginacion y la
/// autorizacion son de los handlers de <c>reporting</c>; esto arma la consulta y devuelve filas.
///
/// La venta no duplica cliente, importes ni numero de cotizacion: todo eso se lee de la
/// <c>Quotation</c> asociada, que es 1:1 (modelo-datos-cotizaciones.md §1.2). Por eso la consulta
/// arranca con un join y no con la tabla de ventas sola.
/// </summary>
internal sealed class SalesReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup) : ISalesReportSource
{
    public async Task<(IReadOnlyList<SalesReportItemDto> Items, int Total)> ListAsync(
        SalesReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(criteria);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (await ToDtosAsync(criteria.TenantId, rows, cancellationToken), total);
    }

    public async Task<IReadOnlyList<SalesReportItemDto>> ListForExportAsync(
        SalesReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await BuildQuery(criteria).Take(limit).ToListAsync(cancellationToken);
        return await ToDtosAsync(criteria.TenantId, rows, cancellationToken);
    }

    private IQueryable<SaleRow> BuildQuery(SalesReportCriteria criteria)
    {
        var sales = quotations.Sales
            .AsNoTracking()
            .Where(sale => sale.TenantId == criteria.TenantId);

        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            sales = sales.Where(sale => sale.ConvertedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            sales = sales.Where(sale => sale.ConvertedAt < end);
        }

        if (criteria.PaymentStatus is { } paymentStatus)
        {
            var mapped = MapPaymentStatus(paymentStatus);
            sales = sales.Where(sale => sale.PaymentStatus == mapped);
        }

        var joined = from sale in sales
                     join quotation in quotations.Quotations.AsNoTracking()
                         on sale.QuotationId equals quotation.Id
                     select new { sale, quotation };

        if (criteria.AdvisorId is { } advisorId)
        {
            var advisor = new MemberId(advisorId);
            joined = joined.Where(row => row.quotation.AdvisorId == advisor);
        }

        if (criteria.ClientId is { } clientId)
        {
            joined = joined.Where(row => row.quotation.ClientId == clientId);
        }

        // Orden explicito y total: sin el, dos paginas consecutivas pueden repetir u omitir
        // filas, porque PostgreSQL no garantiza ningun orden sin ORDER BY. Lo mas nuevo primero,
        // que es como se lee un reporte de ventas, y el numero de venta como desempate porque es
        // unico por tenant.
        return joined
            .OrderByDescending(row => row.sale.ConvertedAt)
            .ThenBy(row => row.sale.SaleNumber)
            .Select(row => new SaleRow(
                row.sale.Id,
                row.sale.SaleNumber,
                row.quotation.Id,
                row.quotation.QuotationNumber,
                row.sale.ConvertedAt,
                row.quotation.AdvisorId,
                row.quotation.ClientId,
                row.sale.Status,
                row.sale.PaymentStatus,
                row.quotation.Subtotal,
                row.quotation.TaxAmount,
                row.quotation.Total));
    }

    // Dos consultas para toda la pagina, no dos por fila.
    private async Task<IReadOnlyList<SalesReportItemDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<SaleRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var advisors = await peopleLookup.EmailsByMembershipIdAsync(
            rows.Select(row => row.AdvisorId.Value).ToArray(), cancellationToken);
        var clients = await clientLookup.FindAsync(
            tenantId, rows.Select(row => row.ClientId).ToArray(), cancellationToken);

        return rows
            .Select(row =>
            {
                clients.TryGetValue(row.ClientId, out var client);
                return new SalesReportItemDto(
                    row.SaleId.Value,
                    row.SaleNumber,
                    row.QuotationId.Value,
                    row.QuotationNumber,
                    row.ConvertedAt,
                    row.AdvisorId.Value,
                    advisors.GetValueOrDefault(row.AdvisorId.Value),
                    row.ClientId,
                    client?.Name,
                    client?.Cuc,
                    row.Status.ToString(),
                    row.PaymentStatus.ToString(),
                    row.Subtotal,
                    row.TaxAmount,
                    row.Total);
            })
            .ToArray();
    }

    // El enum de filtro de reporting se redeclara en su propio dominio (no referencia al de
    // quotations), asi que la traduccion es explicita. Sin default: un valor nuevo del contrato
    // tiene que romper el build aca, no elegir un estado en silencio.
    private static SalePaymentStatus MapPaymentStatus(SalePaymentStatusFilter value) => value switch
    {
        SalePaymentStatusFilter.FullPaymentReceived => SalePaymentStatus.FullPaymentReceived,
        SalePaymentStatusFilter.PartialPaymentReceived => SalePaymentStatus.PartialPaymentReceived,
        SalePaymentStatusFilter.PaymentPending => SalePaymentStatus.PaymentPending,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown payment status.")
    };

    /// <summary>La fila cruda del join, con los tipos del dominio de <c>quotations</c>: los
    /// <c>.Value</c> de los identificadores se desenvuelven ya en memoria, porque EF no traduce
    /// el acceso a un miembro de una propiedad con conversor de valor.</summary>
    private sealed record SaleRow(
        SaleId SaleId,
        string SaleNumber,
        QuotationId QuotationId,
        string QuotationNumber,
        DateTimeOffset ConvertedAt,
        MemberId AdvisorId,
        Guid ClientId,
        SaleStatus Status,
        SalePaymentStatus PaymentStatus,
        decimal Subtotal,
        decimal TaxAmount,
        decimal Total);
}
