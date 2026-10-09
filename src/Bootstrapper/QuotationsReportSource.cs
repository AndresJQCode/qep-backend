using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El origen del reporte de cotizaciones. Ver <see cref="OrdersReportSource"/> sobre por que los
/// adaptadores de <c>reporting</c> viven en el composition root.
///
/// A diferencia del de pedidos, este no necesita join: <c>Quotation</c> ya tiene numero, fechas,
/// asesor, cliente, estado e importes.
/// </summary>
internal sealed class QuotationsReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup,
    ITenantDefaultCurrency tenantDefaultCurrency) : IQuotationsReportSource
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

    /// <summary>
    /// Todo se agrega en la base. Ver <see cref="OrdersReportSource.SummarizeAsync"/> sobre por
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

        // One row per currency: COP and USD are never added together (spec 2026-10-08, Reports).
        var byCurrency = await rows
            .GroupBy(quotation => quotation.Currency)
            .Select(group => new
            {
                Currency = group.Key,
                Count = group.Count(),
                Subtotal = group.Sum(quotation => quotation.Subtotal),
                TaxAmount = group.Sum(quotation => quotation.TaxAmount),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        var quotationCount = byCurrency.Sum(row => row.Count);
        if (quotationCount == 0)
        {
            return new QuotationsReportAggregate(
                0, [], [], [], [], EmptyStatusSlices(), [], EmptyValidity(), []);
        }

        var totals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Total)));
        var defaultCurrency = await tenantDefaultCurrency.GetAsync(criteria.TenantId, cancellationToken);

        var monthly = await SummarizeByMonthAsync(rows, criteria.Period.TimeZone, cancellationToken);
        var byStatus = await SummarizeByStatusAsync(rows, cancellationToken);
        var byAdvisor = await RankAdvisorsAsync(
            rows, options.RankSize, quotationCount, totals, defaultCurrency, cancellationToken);
        var validity = await SummarizeValidityAsync(rows, options.Today, cancellationToken);
        var expiring = await ListExpiringAsync(
            criteria.TenantId, rows, options, defaultCurrency, cancellationToken);

        return new QuotationsReportAggregate(
            quotationCount,
            ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Subtotal))),
            ReportMoney.From(byCurrency.Select(row => (row.Currency, row.TaxAmount))),
            totals,
            monthly,
            byStatus,
            byAdvisor,
            validity,
            expiring);
    }

    /// <summary>La serie mensual por fecha de creación en el mes del tenant (spec 2026-09-17, punto
    /// 5), agrupada en SQL con <c>AT TIME ZONE</c>. Ver <see cref="OrdersReportSource"/>. Sólo vuelven
    /// los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var timeZoneId = timeZone.Id;
        var points = await rows
            .GroupBy(quotation => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(quotation.CreatedAt.UtcDateTime, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(quotation.CreatedAt.UtcDateTime, timeZoneId).Month,
                quotation.Currency,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                group.Key.Currency,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        // Folded in memory: one point per month, its totals per currency.
        return points
            .GroupBy(point => (point.Year, point.Month))
            .OrderBy(month => month.Key.Year)
            .ThenBy(month => month.Key.Month)
            .Select(month => new ReportMonthlyPointDto(
                month.Key.Year,
                month.Key.Month,
                month.Sum(point => point.Count),
                ReportMoney.From(month.Select(point => (point.Currency, point.Total)))))
            .ToArray();
    }

    /// <summary>
    /// El reparto por estado, con **los cinco siempre presentes** aunque alguno este en cero. Sale
    /// de <c>Enum.GetValues</c>, así que un estado nuevo aparece acá sin tocar este método.
    ///
    /// Un estado que desaparece de la respuesta obligaria a la pantalla a saber cuales existen
    /// para poder dibujar el que falta, y eso es duplicar el enum del backend en el frontend.
    /// A status at zero has no money: count 0 and an empty totals list.
    /// </summary>
    private static async Task<IReadOnlyList<ReportStatusSliceDto>> SummarizeByStatusAsync(
        IQueryable<Quotation> rows,
        CancellationToken cancellationToken)
    {
        var slices = await rows
            .GroupBy(quotation => new { quotation.Status, quotation.Currency })
            .Select(group => new
            {
                group.Key.Status,
                group.Key.Currency,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        return Enum.GetValues<QuotationStatus>()
            .Select(status =>
            {
                var ofStatus = slices.Where(slice => slice.Status == status).ToArray();
                return new ReportStatusSliceDto(
                    status.ToString(),
                    ofStatus.Sum(slice => slice.Count),
                    ReportMoney.From(ofStatus.Select(slice => (slice.Currency, slice.Total))));
            })
            .ToArray();
    }

    private static ReportStatusSliceDto[] EmptyStatusSlices() =>
        Enum.GetValues<QuotationStatus>()
            .Select(status => new ReportStatusSliceDto(status.ToString(), 0, []))
            .ToArray();

    private async Task<IReadOnlyList<ReportRankEntryDto>> RankAdvisorsAsync(
        IQueryable<Quotation> rows,
        int rankSize,
        int totalCount,
        IReadOnlyList<ReportMoneyDto> totals,
        string defaultCurrency,
        CancellationToken cancellationToken)
    {
        // Cero significa "no lo traigas": la ventana anterior solo aporta conteo y monto.
        if (rankSize <= 0)
        {
            return [];
        }

        // Plan decision A10: ranked by the total in the tenant default currency, then by count,
        // then by id so two identical calls never swap entries ("blinking" ranking).
        var top = await rows
            .GroupBy(quotation => quotation.AdvisorId)
            .Select(group => new
            {
                AdvisorId = group.Key,
                Count = group.Count(),
                Ranking = group.Sum(quotation => quotation.Currency == defaultCurrency ? quotation.Total : 0m),
            })
            .OrderByDescending(entry => entry.Ranking)
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.AdvisorId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var advisorIds = top.Select(entry => entry.AdvisorId).ToArray();
        var perCurrency = await rows
            .Where(quotation => advisorIds.Contains(quotation.AdvisorId))
            .GroupBy(quotation => new { quotation.AdvisorId, quotation.Currency })
            .Select(group => new
            {
                group.Key.AdvisorId,
                group.Key.Currency,
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        var emails = await peopleLookup.EmailsByMembershipIdAsync(
            advisorIds.Select(id => id.Value).ToArray(), cancellationToken);

        var named = top
            .Select(entry => new ReportRankEntryDto(
                entry.AdvisorId.Value,
                emails.GetValueOrDefault(entry.AdvisorId.Value),
                Secondary: null,
                EntityCount: 1,
                entry.Count,
                ReportMoney.From(perCurrency
                    .Where(total => total.AdvisorId == entry.AdvisorId)
                    .Select(total => (total.Currency, total.Total)))))
            .ToList();

        var others = await ReportRankFolding.FoldOthersAsync(
            named, rankSize, totalCount, totals,
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
    /// tramo (and by currency, so each bucket carries its totals per currency), en vez de cinco
    /// consultas con cinco <c>WHERE</c> distintos.
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
            .GroupBy(quotation => new
            {
                Bucket = quotation.ValidUntil == null ? ValidityBucket.WithoutExpiry
                    : quotation.ValidUntil < today ? ValidityBucket.Expired
                    : quotation.ValidUntil <= weekEnd ? ValidityBucket.WithinSevenDays
                    : quotation.ValidUntil <= monthEnd ? ValidityBucket.WithinThirtyDays
                    : ValidityBucket.Beyond,
                quotation.Currency,
            })
            .Select(group => new
            {
                group.Key.Bucket,
                group.Key.Currency,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .ToListAsync(cancellationToken);

        ReportBucketDto Read(ValidityBucket key)
        {
            var ofBucket = buckets.Where(bucket => bucket.Bucket == key).ToArray();
            return new ReportBucketDto(
                ofBucket.Sum(bucket => bucket.Count),
                ReportMoney.From(ofBucket.Select(bucket => (bucket.Currency, bucket.Total))));
        }

        return new QuotationValidityDto(
            Read(ValidityBucket.Expired),
            Read(ValidityBucket.WithinSevenDays),
            Read(ValidityBucket.WithinThirtyDays),
            Read(ValidityBucket.Beyond),
            Read(ValidityBucket.WithoutExpiry).Count);
    }

    private static QuotationValidityDto EmptyValidity() =>
        new(
            new ReportBucketDto(0, []),
            new ReportBucketDto(0, []),
            new ReportBucketDto(0, []),
            new ReportBucketDto(0, []),
            0);

    /// <summary>
    /// La cola de vencimientos: las enviadas que vencen dentro de la ventana, **ordenadas por
    /// monto** — lo que decide a cual llamar primero es la plata en juego, no la fecha.
    /// Default-currency quotations first (plan decision A10): amounts only compare within one
    /// currency.
    ///
    /// Respeta los filtros del reporte: si alguien esta mirando las cotizaciones de un asesor,
    /// la cola es la de ese asesor y no la de todos.
    /// </summary>
    private async Task<IReadOnlyList<QuotationExpiringDto>> ListExpiringAsync(
        Guid tenantId,
        IQueryable<Quotation> rows,
        QuotationsSummaryOptions options,
        string defaultCurrency,
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
            .OrderBy(quotation => quotation.Currency == defaultCurrency ? 0 : 1)
            .ThenByDescending(quotation => quotation.Total)
            .ThenBy(quotation => quotation.QuotationNumber)
            .Take(options.ExpiringSize)
            .Select(quotation => new ExpiringRow(
                quotation.Id,
                quotation.QuotationNumber,
                quotation.ValidUntil,
                quotation.AdvisorId,
                quotation.ClientId,
                quotation.Currency,
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
                    row.Currency,
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
        string Currency,
        decimal Total);

    /// <summary>
    /// El conjunto filtrado, sin ordenar ni proyectar. Lo comparten el listado y el resumen:
    /// que los dos partan de la misma consulta es lo que hace imposible que el panel sume sobre
    /// un conjunto y la tabla muestre otro.
    ///
    /// Devuelve la entidad y no <c>QuotationRow</c> porque EF no traduce un agregado sobre
    /// una proyeccion a un record: no ve a traves del constructor. Ver
    /// <see cref="OrdersReportSource"/>.
    /// </summary>
    private IQueryable<Quotation> FilterQuotations(QuotationsReportCriteria criteria)
    {
        var query = quotations.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == criteria.TenantId);

        // Instantes ya cortados en el día del tenant (spec 2026-09-17, punto 4): acá no se decide huso.
        if (criteria.Period.Start is { } start)
        {
            query = query.Where(quotation => quotation.CreatedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
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

    /// <summary>Ver OrdersReportSource: sin ORDER BY total, dos paginas consecutivas pueden
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
                quotation.Currency,
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
                    row.Currency,
                    row.Subtotal,
                    row.TaxAmount,
                    row.Total);
            })
            .ToArray();
    }

    // Sin default a proposito: ver MapPaymentStatus en OrdersReportSource.
    private static QuotationStatus MapStatus(QuotationStatusFilter value) => value switch
    {
        QuotationStatusFilter.Draft => QuotationStatus.Draft,
        QuotationStatusFilter.Sent => QuotationStatus.Sent,
        QuotationStatusFilter.Expired => QuotationStatus.Expired,
        QuotationStatusFilter.Voided => QuotationStatus.Voided,
        QuotationStatusFilter.Converted => QuotationStatus.Converted,
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
        string Currency,
        decimal Subtotal,
        decimal TaxAmount,
        decimal Total);
}
