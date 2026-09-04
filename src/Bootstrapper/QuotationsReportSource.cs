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

    /// <summary>
    /// Todo se agrega en la base. Ver <see cref="SalesReportSource.SummarizeAsync"/> sobre por
    /// que se agrega sobre la entidad y no sobre <c>QuotationRow</c>: EF no ve a traves del
    /// constructor de un record y falla en tiempo de ejecucion.
    ///
    /// Los tramos de vigencia y la cola de vencimientos se filtran por **fechas constantes**
    /// calculadas aca a partir de <c>options.Today</c>, y no por aritmetica de fechas en SQL: una
    /// comparacion contra una constante usa el indice y traduce igual en cualquier version de
    /// Npgsql.
    /// </summary>
    public async Task<QuotationsReportAggregate> SummarizeAsync(
        QuotationsReportCriteria criteria,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        var rows = FilterQuotations(criteria);

        var totals = await rows
            .GroupBy(_ => 1)
            .Select(group => new
            {
                QuotationCount = group.Count(),
                Subtotal = group.Sum(quotation => quotation.Subtotal),
                TaxAmount = group.Sum(quotation => quotation.TaxAmount),
                Total = group.Sum(quotation => quotation.Total),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (totals is null || totals.QuotationCount == 0)
        {
            return new QuotationsReportAggregate(
                0, 0m, 0m, 0m, [], EmptyStatusSlices(), [], EmptyValidity(), []);
        }

        var monthly = await SummarizeByMonthAsync(rows, cancellationToken);
        var byStatus = await SummarizeByStatusAsync(rows, cancellationToken);
        var byAdvisor = await RankAdvisorsAsync(
            rows, options.RankSize, totals.QuotationCount, totals.Total, cancellationToken);
        var validity = await SummarizeValidityAsync(rows, options.Today, cancellationToken);
        var expiring = await ListExpiringAsync(
            criteria.TenantId, rows, options, cancellationToken);

        return new QuotationsReportAggregate(
            totals.QuotationCount, totals.Subtotal, totals.TaxAmount, totals.Total,
            monthly, byStatus, byAdvisor, validity, expiring);
    }

    /// <summary>La serie mensual por fecha de creacion, en UTC — mismo huso en el que
    /// <see cref="ReportDateRange"/> corta el rango. Solo vuelven los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        CancellationToken cancellationToken)
    {
        var months = await rows
            .GroupBy(quotation => new
            {
                quotation.CreatedAt.UtcDateTime.Year,
                quotation.CreatedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportMonthlyPointDto(
                point.Year, point.Month, point.Count, point.Total))
            .ToArray();
    }

    /// <summary>
    /// El reparto por estado, con **los cuatro siempre presentes** aunque alguno este en cero.
    ///
    /// Un estado que desaparece de la respuesta obligaria a la pantalla a saber cuales existen
    /// para poder dibujar el que falta, y eso es duplicar el enum del backend en el frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportStatusSliceDto>> SummarizeByStatusAsync(
        IQueryable<Quotation> rows,
        CancellationToken cancellationToken)
    {
        var slices = await rows
            .GroupBy(quotation => quotation.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        var found = slices.ToDictionary(slice => slice.Status);

        return Enum.GetValues<QuotationStatus>()
            .Select(status => found.TryGetValue(status, out var slice)
                ? new ReportStatusSliceDto(status.ToString(), slice.Count, slice.Total)
                : new ReportStatusSliceDto(status.ToString(), 0, 0m))
            .ToArray();
    }

    private static ReportStatusSliceDto[] EmptyStatusSlices() =>
        Enum.GetValues<QuotationStatus>()
            .Select(status => new ReportStatusSliceDto(status.ToString(), 0, 0m))
            .ToArray();

    private async Task<IReadOnlyList<ReportRankEntryDto>> RankAdvisorsAsync(
        IQueryable<Quotation> rows,
        int rankSize,
        int totalCount,
        decimal totalAmount,
        CancellationToken cancellationToken)
    {
        // Cero significa "no lo traigas": la ventana anterior solo aporta conteo y monto.
        if (rankSize <= 0)
        {
            return [];
        }

        var top = await rows
            .GroupBy(quotation => quotation.AdvisorId)
            .Select(group => new
            {
                AdvisorId = group.Key,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            // Desempate por id: sin orden total, dos asesores con el mismo monto se intercambian
            // entre dos llamadas identicas y el ranking "parpadea".
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.AdvisorId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var emails = await peopleLookup.EmailsByMembershipIdAsync(
            top.Select(entry => entry.AdvisorId.Value).ToArray(), cancellationToken);

        var named = top
            .Select(entry => new ReportRankEntryDto(
                entry.AdvisorId.Value,
                emails.GetValueOrDefault(entry.AdvisorId.Value),
                Secondary: null,
                EntityCount: 1,
                entry.Count,
                entry.Total))
            .ToList();

        var others = await ReportRankFolding.FoldOthersAsync(
            named, rankSize, totalCount, totalAmount,
            () => rows.Select(quotation => quotation.AdvisorId)
                .Distinct()
                .CountAsync(cancellationToken));
        if (others is not null)
        {
            named.Add(others);
        }

        return named;
    }

    /// <summary>
    /// Los tramos de vigencia, **solo sobre las enviadas**: un borrador no vence porque no salio,
    /// y una anulada ya no interesa.
    ///
    /// Se resuelve en una sola consulta que clasifica cada fila en un tramo y agrupa por ese
    /// tramo, en vez de cinco consultas con cinco <c>WHERE</c> distintos.
    /// </summary>
    private static async Task<QuotationValidityDto> SummarizeValidityAsync(
        IQueryable<Quotation> rows,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var weekEnd = today.AddDays(7);
        var monthEnd = today.AddDays(30);

        var buckets = await rows
            .Where(quotation => quotation.Status == QuotationStatus.Sent)
            .GroupBy(quotation =>
                quotation.ValidUntil == null ? ValidityBucket.WithoutExpiry
                : quotation.ValidUntil < today ? ValidityBucket.Expired
                : quotation.ValidUntil <= weekEnd ? ValidityBucket.WithinSevenDays
                : quotation.ValidUntil <= monthEnd ? ValidityBucket.WithinThirtyDays
                : ValidityBucket.Beyond)
            .Select(group => new
            {
                Bucket = group.Key,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        var found = buckets.ToDictionary(bucket => bucket.Bucket);

        ReportBucketDto Read(ValidityBucket key) =>
            found.TryGetValue(key, out var bucket)
                ? new ReportBucketDto(bucket.Count, bucket.Total)
                : new ReportBucketDto(0, 0m);

        return new QuotationValidityDto(
            Read(ValidityBucket.Expired),
            Read(ValidityBucket.WithinSevenDays),
            Read(ValidityBucket.WithinThirtyDays),
            Read(ValidityBucket.Beyond),
            Read(ValidityBucket.WithoutExpiry).Count);
    }

    private static QuotationValidityDto EmptyValidity() =>
        new(
            new ReportBucketDto(0, 0m),
            new ReportBucketDto(0, 0m),
            new ReportBucketDto(0, 0m),
            new ReportBucketDto(0, 0m),
            0);

    /// <summary>
    /// La cola de vencimientos: las enviadas que vencen dentro de la ventana, **ordenadas por
    /// monto** — lo que decide a cual llamar primero es la plata en juego, no la fecha.
    ///
    /// Respeta los filtros del reporte: si alguien esta mirando las cotizaciones de un asesor,
    /// la cola es la de ese asesor y no la de todos.
    /// </summary>
    private async Task<IReadOnlyList<QuotationExpiringDto>> ListExpiringAsync(
        Guid tenantId,
        IQueryable<Quotation> rows,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ExpiringSize <= 0)
        {
            return [];
        }

        var today = options.Today;
        var limit = today.AddDays(options.ExpiringWithinDays);

        var soon = await rows
            .Where(quotation =>
                quotation.Status == QuotationStatus.Sent
                && quotation.ValidUntil != null
                && quotation.ValidUntil >= today
                && quotation.ValidUntil <= limit)
            .OrderByDescending(quotation => quotation.Total)
            .ThenBy(quotation => quotation.QuotationNumber)
            .Take(options.ExpiringSize)
            .Select(quotation => new ExpiringRow(
                quotation.Id,
                quotation.QuotationNumber,
                quotation.ValidUntil,
                quotation.AdvisorId,
                quotation.ClientId,
                quotation.Total))
            .ToListAsync(cancellationToken);

        if (soon.Count == 0)
        {
            return [];
        }

        var advisors = await peopleLookup.EmailsByMembershipIdAsync(
            soon.Select(row => row.AdvisorId.Value).ToArray(), cancellationToken);
        var clients = await clientLookup.FindAsync(
            tenantId, soon.Select(row => row.ClientId).ToArray(), cancellationToken);

        return soon
            .Select(row =>
            {
                clients.TryGetValue(row.ClientId, out var client);
                // `ValidUntil` no es nulo acá: el WHERE ya lo excluyó. El `!` es por el tipo,
                // no por una suposición.
                var validUntil = row.ValidUntil!.Value;
                return new QuotationExpiringDto(
                    row.QuotationId.Value,
                    row.QuotationNumber,
                    validUntil,
                    validUntil.DayNumber - today.DayNumber,
                    client?.Name,
                    client?.Cuc,
                    advisors.GetValueOrDefault(row.AdvisorId.Value),
                    row.Total);
            })
            .ToArray();
    }

    /// <summary>Los tramos como enum y no como texto: la clave de un <c>GROUP BY</c> tiene que
    /// ser comparable, y un typo en una cadena seria un tramo fantasma en silencio.</summary>
    private enum ValidityBucket
    {
        Expired,
        WithinSevenDays,
        WithinThirtyDays,
        Beyond,
        WithoutExpiry,
    }

    private sealed record ExpiringRow(
        QuotationId QuotationId,
        string QuotationNumber,
        DateOnly? ValidUntil,
        MemberId AdvisorId,
        Guid ClientId,
        decimal Total);

    /// <summary>
    /// El conjunto filtrado, sin ordenar ni proyectar. Lo comparten el listado, la
    /// exportacion y el resumen: que los tres partan de la misma consulta es lo que hace
    /// imposible que el panel sume sobre un conjunto y la tabla muestre otro.
    ///
    /// Devuelve la entidad y no <c>QuotationRow</c> porque EF no traduce un agregado sobre
    /// una proyeccion a un record: no ve a traves del constructor. Ver
    /// <see cref="SalesReportSource"/>.
    /// </summary>
    private IQueryable<Quotation> FilterQuotations(QuotationsReportCriteria criteria)
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

        return query;
    }

    /// <summary>Ver SalesReportSource: sin ORDER BY total, dos paginas consecutivas pueden
    /// repetir u omitir filas.</summary>
    private IQueryable<QuotationRow> BuildQuery(QuotationsReportCriteria criteria) =>
        FilterQuotations(criteria)
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
