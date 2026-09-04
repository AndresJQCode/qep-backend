using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Bootstrapper;

/// <summary>
/// El origen del reporte de cotizaciones. Ver <see cref="SalesReportSource"/> sobre por que los
/// adaptadores de <c>reporting</c> viven en el composition root.
///
/// A diferencia del de ventas, este no necesita join: <c>Quotation</c> ya tiene numero, fechas,
/// asesor, cliente, estado e importes.
/// </summary>
internal sealed class QuotationsReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup) : IQuotationsReportSource
{
    public async Task<(IReadOnlyList<QuotationsReportItemDto> Items, int Total)> ListAsync(
        QuotationsReportCriteria criteria,
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

    public async Task<IReadOnlyList<QuotationsReportItemDto>> ListForExportAsync(
        QuotationsReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await BuildQuery(criteria).Take(limit).ToListAsync(cancellationToken);
        return await ToDtosAsync(criteria.TenantId, rows, cancellationToken);
    }

    private IQueryable<QuotationRow> BuildQuery(QuotationsReportCriteria criteria)
    {
        var query = quotations.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == criteria.TenantId);

        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            query = query.Where(quotation => quotation.CreatedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            query = query.Where(quotation => quotation.CreatedAt < end);
        }

        if (criteria.AdvisorId is { } advisorId)
        {
            var advisor = new MemberId(advisorId);
            query = query.Where(quotation => quotation.AdvisorId == advisor);
        }

        if (criteria.ClientId is { } clientId)
        {
            query = query.Where(quotation => quotation.ClientId == clientId);
        }

        if (criteria.Status is { } status)
        {
            var mapped = MapStatus(status);
            query = query.Where(quotation => quotation.Status == mapped);
        }

        // Ver SalesReportSource: sin ORDER BY total, dos paginas consecutivas pueden repetir u
        // omitir filas.
        return query
            .OrderByDescending(quotation => quotation.CreatedAt)
            .ThenBy(quotation => quotation.QuotationNumber)
            .Select(quotation => new QuotationRow(
                quotation.Id,
                quotation.QuotationNumber,
                quotation.CreatedAt,
                quotation.ValidUntil,
                quotation.AdvisorId,
                quotation.ClientId,
                quotation.Status,
                quotation.Subtotal,
                quotation.TaxAmount,
                quotation.Total));
    }

    private async Task<IReadOnlyList<QuotationsReportItemDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<QuotationRow> rows,
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
                return new QuotationsReportItemDto(
                    row.QuotationId.Value,
                    row.QuotationNumber,
                    row.CreatedAt,
                    row.ValidUntil,
                    row.AdvisorId.Value,
                    advisors.GetValueOrDefault(row.AdvisorId.Value),
                    row.ClientId,
                    client?.Name,
                    client?.Cuc,
                    row.Status.ToString(),
                    row.Subtotal,
                    row.TaxAmount,
                    row.Total);
            })
            .ToArray();
    }

    // Sin default a proposito: ver MapPaymentStatus en SalesReportSource.
    private static QuotationStatus MapStatus(QuotationStatusFilter value) => value switch
    {
        QuotationStatusFilter.Draft => QuotationStatus.Draft,
        QuotationStatusFilter.Sent => QuotationStatus.Sent,
        QuotationStatusFilter.Expired => QuotationStatus.Expired,
        QuotationStatusFilter.Voided => QuotationStatus.Voided,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown status.")
    };

    private sealed record QuotationRow(
        QuotationId QuotationId,
        string QuotationNumber,
        DateTimeOffset CreatedAt,
        DateOnly? ValidUntil,
        MemberId AdvisorId,
        Guid ClientId,
        QuotationStatus Status,
        decimal Subtotal,
        decimal TaxAmount,
        decimal Total);
}
