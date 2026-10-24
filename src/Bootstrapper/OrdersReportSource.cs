using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;
using Modules.Tenancy.Application;

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
/// **No decide nada.** La normalizacion de la paginacion, los topes del resumen y la autorizacion
/// son de los handlers de <c>reporting</c>; esto arma la consulta y devuelve filas.
///
/// Desde el spec 2026-10-15 el pedido tiene sus propios cliente, asesor, moneda e importes, y
/// puede no tener cotización (manual o de una integración): todo se lee del pedido, sin join. El
/// número de la cotización de origen, cuando la hay, se resuelve aparte para la página.
/// </summary>
internal sealed class OrdersReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup,
    ITenantDefaultCurrency tenantDefaultCurrency) : IOrdersReportSource
{
    public async Task<(IReadOnlyList<OrdersReportItemDto> Items, int Total)> ListAsync(
        OrdersReportCriteria criteria,
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
    /// Todo se agrega **en la base**: ningun camino de aca trae filas para sumarlas en memoria,
    /// que es justo lo que el resumen existe para evitar.
    ///
    /// Se agrega sobre la entidad <see cref="Order"/> y no sobre <see cref="OrderRow"/>: EF no ve
    /// a traves del constructor de un record y falla con "could not be translated".
    ///
    /// Son varias consultas y no una porque son agregaciones de distinta granularidad —los totales
    /// por moneda, la serie por mes y dos rankings—; una sola que las mezclara necesitaria funciones de
    /// ventana que EF no arma. Todas atacan el mismo indice.
    /// </summary>
    public async Task<OrdersReportAggregate> SummarizeAsync(
        OrdersReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        // Un pedido anulado no es una venta (spec 2026-09-16, decisión 6): no suma en el total, la
        // serie ni los rankings, y tampoco en el período anterior, que pasa por este mismo método.
        // El listado (BuildQuery) no lo filtra: ahí el anulado se ve con su estado.
        var orders = FilterOrders(criteria).Where(candidate => candidate.Status != OrderStatus.Cancelled);

        // One row per currency: COP and USD are never added together (spec 2026-10-08, Reports).
        // Grouping by currency also replaces the old GroupBy over a constant: zero orders give zero
        // rows, and the count below is zero.
        var byCurrency = await orders
            .GroupBy(row => row.Currency)
            .Select(group => new
            {
                Currency = group.Key,
                Count = group.Count(),
                Subtotal = group.Sum(row => row.Subtotal),
                TaxAmount = group.Sum(row => row.TaxAmount),
                FreightSubtotal = group.Sum(row => row.FreightSubtotal),
                FreightTaxAmount = group.Sum(row => row.FreightTaxAmount),
                Total = group.Sum(row => row.Total),
            })
            .ToListAsync(cancellationToken);

        // Sin pedidos no hay nada que agrupar ni ninguna etiqueta que resolver: cinco consultas
        // menos, y el resto del metodo tendria que tratar el cero como caso especial igual.
        var orderCount = byCurrency.Sum(row => row.Count);
        if (orderCount == 0)
        {
            return new OrdersReportAggregate(
                0, [], [], [], [], [], [], [], [], CompleteBySource([]), new ReportWithoutClientDto(0, []));
        }

        var sourceRows = await orders
            .GroupBy(row => new { row.Source, row.Currency })
            .Select(group => new
            {
                group.Key.Source,
                group.Key.Currency,
                Count = group.Count(),
                Total = group.Sum(row => row.Total),
            })
            .ToListAsync(cancellationToken);
        var bySource = CompleteBySource(sourceRows
            .Select(row => (row.Source.ToString(), row.Currency, row.Count, row.Total))
            .ToArray());

        var subtotals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Subtotal)));
        var taxAmounts = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.TaxAmount)));
        var freightSubtotals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.FreightSubtotal)));
        var freightTaxAmounts = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.FreightTaxAmount)));
        var totals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Total)));

        // La serie mensual va en el mes del tenant (spec 2026-09-17, punto 5): el pedido de las 20:00
        // del último día en Bogotá es de ese mes, aunque en UTC ya sea el siguiente. Se agrupa en SQL
        // con `AT TIME ZONE` y el ID IANA del tenant —lo que Npgsql traduce desde
        // TimeZoneInfo.ConvertTimeBySystemTimeZoneId sobre UtcDateTime (spike de la Task 7)—, nunca
        // en el huso de la sesión de PostgreSQL, que haría depender el resultado de la conexión.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var timeZoneId = criteria.Period.TimeZone.Id;
        var monthRows = await orders
            .GroupBy(row => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.ConvertedAt.UtcDateTime, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.ConvertedAt.UtcDateTime, timeZoneId).Month,
                row.Currency,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                group.Key.Currency,
                Count = group.Count(),
                Total = group.Sum(row => row.Total),
            })
            .ToListAsync(cancellationToken);

        // Folded in memory: one point per month, its totals per currency.
        var monthly = monthRows
            .GroupBy(point => (point.Year, point.Month))
            .OrderBy(month => month.Key.Year)
            .ThenBy(month => month.Key.Month)
            .Select(month => new ReportMonthlyPointDto(
                month.Key.Year,
                month.Key.Month,
                month.Sum(point => point.Count),
                ReportMoney.From(month.Select(point => (point.Currency, point.Total)))))
            .ToArray();

        // Ranked by the total in the tenant default currency (plan decision A10): a single-currency
        // tenant ranks exactly as before, and a mixed one has a deterministic order that never adds
        // two currencies. Count and id break ties so the ranking does not "blink" between two
        // identical calls.
        var defaultCurrency = await tenantDefaultCurrency.GetAsync(criteria.TenantId, cancellationToken);

        var topAdvisors = await orders
            .GroupBy(row => row.AdvisorId)
            .Select(group => new
            {
                AdvisorId = group.Key,
                Count = group.Count(),
                Ranking = group.Sum(row => row.Currency == defaultCurrency ? row.Total : 0m),
            })
            .OrderByDescending(entry => entry.Ranking)
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.AdvisorId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var advisorIds = topAdvisors.Select(entry => entry.AdvisorId).ToArray();
        var advisorTotals = await orders
            .Where(row => advisorIds.Contains(row.AdvisorId))
            .GroupBy(row => new { row.AdvisorId, row.Currency })
            .Select(group => new
            {
                group.Key.AdvisorId,
                group.Key.Currency,
                Total = group.Sum(row => row.Total),
            })
            .ToListAsync(cancellationToken);

        var emails = await peopleLookup.EmailsByMembershipIdAsync(
            advisorIds.Select(id => id.Value).ToArray(), cancellationToken);

        var byAdvisor = topAdvisors
            .Select(entry => new ReportRankEntryDto(
                entry.AdvisorId.Value,
                emails.GetValueOrDefault(entry.AdvisorId.Value),
                Secondary: null,
                EntityCount: 1,
                entry.Count,
                ReportMoney.From(advisorTotals
                    .Where(total => total.AdvisorId == entry.AdvisorId)
                    .Select(total => (total.Currency, total.Total)))))
            .ToList();

        var otherAdvisors = await ReportRankFolding.FoldOthersAsync(
            byAdvisor, rankSize, orderCount, totals,
            () => orders
                .Select(row => row.AdvisorId)
                .Distinct()
                .CountAsync(cancellationToken));
        if (otherAdvisors is not null)
        {
            byAdvisor.Add(otherAdvisors);
        }

        var topClients = await orders
            .GroupBy(row => row.ClientId)
            .Select(group => new
            {
                ClientId = group.Key,
                Count = group.Count(),
                Ranking = group.Sum(row => row.Currency == defaultCurrency ? row.Total : 0m),
            })
            .OrderByDescending(entry => entry.Ranking)
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.ClientId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var clientIds = topClients.Select(entry => entry.ClientId).ToArray();
        var clientTotals = await orders
            .Where(row => clientIds.Contains(row.ClientId))
            .GroupBy(row => new { row.ClientId, row.Currency })
            .Select(group => new
            {
                group.Key.ClientId,
                group.Key.Currency,
                Total = group.Sum(row => row.Total),
            })
            .ToListAsync(cancellationToken);

        var clients = await clientLookup.FindAsync(criteria.TenantId, clientIds, cancellationToken);

        var byClient = topClients
            .Select(entry =>
            {
                clients.TryGetValue(entry.ClientId, out var client);
                return new ReportRankEntryDto(
                    entry.ClientId,
                    client?.Name,
                    client?.Cuc,
                    EntityCount: 1,
                    entry.Count,
                    ReportMoney.From(clientTotals
                        .Where(total => total.ClientId == entry.ClientId)
                        .Select(total => (total.Currency, total.Total))));
            })
            .ToList();

        var otherClients = await ReportRankFolding.FoldOthersAsync(
            byClient, rankSize, orderCount, totals,
            () => orders
                .Select(row => row.ClientId)
                .Distinct()
                .CountAsync(cancellationToken));
        if (otherClients is not null)
        {
            byClient.Add(otherClients);
        }

        return new OrdersReportAggregate(
            orderCount,
            subtotals,
            taxAmounts,
            freightSubtotals,
            freightTaxAmounts,
            totals,
            monthly,
            byAdvisor,
            byClient,
            bySource,
            new ReportWithoutClientDto(0, []));
    }

    /// <summary>
    /// Los pedidos del tenant filtrados por el criterio: rango de conversion, estado de pago,
    /// asesor y cliente. Todo es propio del pedido (spec 2026-10-15).
    /// </summary>
    private IQueryable<Order> FilterOrders(OrdersReportCriteria criteria)
    {
        var orders = quotations.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == criteria.TenantId);

        // Instantes ya cortados en el día del tenant (spec 2026-09-17, punto 4): acá no se decide huso.
        if (criteria.Period.Start is { } start)
        {
            orders = orders.Where(order => order.ConvertedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            orders = orders.Where(order => order.ConvertedAt < end);
        }

        if (criteria.PaymentStatus is { } paymentStatus)
        {
            var mapped = MapPaymentStatus(paymentStatus);
            orders = orders.Where(order => order.PaymentStatus == mapped);
        }

        if (criteria.Source is { } source)
        {
            if (MapSource(source) is { } mapped)
            {
                orders = orders.Where(order => order.Source == mapped);
            }
            else
            {
                // Pos no es un pedido: no hay ninguno de ese origen en quotations.orders.
                orders = orders.Where(_ => false);
            }
        }

        if (criteria.AdvisorId is { } advisorId)
        {
            var advisor = new MemberId(advisorId);
            orders = orders.Where(order => order.AdvisorId == advisor);
        }

        if (criteria.ClientId is { } clientId)
        {
            orders = orders.Where(order => order.ClientId == clientId);
        }

        return orders;
    }

    /// <summary>
    /// El conjunto ya ordenado, que es lo que necesita el listado.
    ///
    /// Orden explicito y total: sin el, dos paginas consecutivas pueden repetir u omitir filas,
    /// porque PostgreSQL no garantiza ningun orden sin <c>ORDER BY</c>. Lo mas nuevo primero, que
    /// es como se lee un reporte de pedidos, y el numero de pedido como desempate porque es unico
    /// por tenant.
    /// </summary>
    private IQueryable<OrderRow> BuildQuery(OrdersReportCriteria criteria)
    {
        return FilterOrders(criteria)
            .OrderByDescending(row => row.ConvertedAt)
            .ThenBy(row => row.OrderNumber)
            .Select(row => new OrderRow(
                row.Id,
                row.OrderNumber,
                row.Source,
                row.QuotationId,
                row.ConvertedAt,
                row.AdvisorId,
                row.ClientId,
                row.Status,
                row.PaymentStatus,
                row.Currency,
                row.Subtotal,
                row.TaxAmount,
                row.FreightSubtotal,
                row.FreightTaxAmount,
                row.Total));
    }

    // Tres consultas para toda la pagina, no tres por fila.
    private async Task<IReadOnlyList<OrdersReportItemDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<OrderRow> rows,
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
        var quotationIds = rows
            .Where(row => row.QuotationId is not null)
            .Select(row => row.QuotationId!.Value)
            .Distinct()
            .ToArray();
        var quotationNumbers = quotationIds.Length == 0
            ? new Dictionary<QuotationId, string>()
            : await quotations.Quotations
                .AsNoTracking()
                .Where(quotation => quotation.TenantId == tenantId && quotationIds.Contains(quotation.Id))
                .ToDictionaryAsync(quotation => quotation.Id, quotation => quotation.QuotationNumber, cancellationToken);

        return rows
            .Select(row =>
            {
                clients.TryGetValue(row.ClientId, out var client);
                return new OrdersReportItemDto(
                    row.OrderId.Value,
                    row.OrderNumber,
                    row.Source.ToString(),
                    row.QuotationId?.Value,
                    row.QuotationId is { } quotationId ? quotationNumbers.GetValueOrDefault(quotationId) : null,
                    row.ConvertedAt,
                    row.AdvisorId.Value,
                    advisors.GetValueOrDefault(row.AdvisorId.Value),
                    row.ClientId,
                    client?.Name,
                    client?.Cuc,
                    row.Status.ToString(),
                    row.PaymentStatus.ToString(),
                    row.Currency,
                    row.Subtotal,
                    row.TaxAmount,
                    row.FreightSubtotal,
                    row.FreightTaxAmount,
                    row.Total);
            })
            .ToArray();
    }

    // El enum de filtro de reporting se redeclara en su propio dominio (no referencia al de
    // quotations), asi que la traduccion es explicita. Sin default: un valor nuevo del contrato
    // tiene que romper el build aca, no elegir un estado en silencio.
    private static OrderPaymentStatus MapPaymentStatus(OrderPaymentStatusFilter value) => value switch
    {
        OrderPaymentStatusFilter.FullPaymentReceived => OrderPaymentStatus.FullPaymentReceived,
        OrderPaymentStatusFilter.PartialPaymentReceived => OrderPaymentStatus.PartialPaymentReceived,
        OrderPaymentStatusFilter.PaymentPending => OrderPaymentStatus.PaymentPending,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown payment status.")
    };

    // Mismo criterio que MapPaymentStatus. Pos no tiene equivalente en OrderSource: es una venta.
    private static OrderSource? MapSource(OrderSourceFilter value) => value switch
    {
        OrderSourceFilter.Quotation => OrderSource.Quotation,
        OrderSourceFilter.Manual => OrderSource.Manual,
        OrderSourceFilter.Shopify => OrderSource.Shopify,
        OrderSourceFilter.Pos => null,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown order source.")
    };

    // Las cuatro entradas siempre, en el orden del enum, con cero donde no hubo nada.
    private static ReportSourceSliceDto[] CompleteBySource(
        IReadOnlyList<(string Source, string Currency, int Count, decimal Total)> rows) =>
        Enum.GetValues<OrderSourceFilter>()
            .Select(source =>
            {
                var name = source.ToString();
                var matching = rows.Where(row => row.Source == name).ToArray();
                return new ReportSourceSliceDto(
                    name,
                    matching.Sum(row => row.Count),
                    ReportMoney.From(matching.Select(row => (row.Currency, row.Total))));
            })
            .ToArray();

    /// <summary>La fila cruda del pedido, con los tipos del dominio de <c>quotations</c>: los
    /// <c>.Value</c> de los identificadores se desenvuelven ya en memoria, porque EF no traduce
    /// el acceso a un miembro de una propiedad con conversor de valor.</summary>
    private sealed record OrderRow(
        OrderId OrderId,
        string OrderNumber,
        OrderSource Source,
        QuotationId? QuotationId,
        DateTimeOffset ConvertedAt,
        MemberId AdvisorId,
        Guid ClientId,
        OrderStatus Status,
        OrderPaymentStatus PaymentStatus,
        string Currency,
        decimal Subtotal,
        decimal TaxAmount,
        decimal FreightSubtotal,
        decimal FreightTaxAmount,
        decimal Total);
}
