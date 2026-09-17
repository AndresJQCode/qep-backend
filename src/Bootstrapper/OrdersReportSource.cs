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
/// **No decide nada.** La normalizacion de la paginacion, los topes del resumen y la autorizacion
/// son de los handlers de <c>reporting</c>; esto arma la consulta y devuelve filas.
///
/// El pedido no duplica cliente, importes ni numero de cotizacion: todo eso se lee de la
/// <c>Quotation</c> asociada, que es 1:1 (modelo-datos-cotizaciones.md §1.2). Por eso la consulta
/// arranca con un join y no con la tabla de pedidos sola.
/// </summary>
internal sealed class OrdersReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup) : IOrdersReportSource
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
    /// Las cuatro consultas viven en un solo metodo por una razon de EF, no de estilo: se agrega
    /// sobre el **tipo anonimo del join**, y un tipo anonimo no puede cruzar una frontera de
    /// metodo. Proyectarlo antes a un record nombrado —que fue el primer intento— hace que EF no
    /// vea a traves del constructor y falle con "could not be translated" en tiempo de ejecucion.
    ///
    /// Son varias consultas y no una porque son agregaciones de distinta granularidad —el total,
    /// la serie por mes y dos rankings—; una sola que las mezclara necesitaria funciones de
    /// ventana que EF no arma. Todas atacan el mismo indice.
    /// </summary>
    public async Task<OrdersReportAggregate> SummarizeAsync(
        OrdersReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        // Un pedido anulado no es una venta (spec 2026-09-16, decisión 6): no suma en el total, la
        // serie ni los rankings, y tampoco en el período anterior, que pasa por este mismo método.
        // Se filtra antes del join para que llegue a la base como WHERE. El listado (BuildQuery) no
        // lo filtra: ahí el anulado se ve con su estado.
        var joined = from order in FilterOrders(criteria)
                         .Where(candidate => candidate.Status != OrderStatus.Cancelled)
                     join quotation in FilterQuotations(criteria)
                         on order.QuotationId equals quotation.Id
                     select new { order, quotation };

        // GroupBy sobre una constante es el "agregar todo el conjunto", que traduce a un SELECT
        // con agregados y sin GROUP BY. Sobre cero filas no devuelve ninguna, y de ahi el
        // fallback: un resumen vacio es cero, nunca nulo.
        var totals = await joined
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Subtotal = group.Sum(row => row.quotation.Subtotal),
                TaxAmount = group.Sum(row => row.quotation.TaxAmount),
                Total = group.Sum(row => row.quotation.Total),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Sin pedidos no hay nada que agrupar ni ninguna etiqueta que resolver: cinco consultas
        // menos, y el resto del metodo tendria que tratar el cero como caso especial igual.
        if (totals is null || totals.Count == 0)
        {
            return new OrdersReportAggregate(0, 0m, 0m, 0m, [], [], []);
        }

        // La serie mensual va en **UTC**, el mismo huso en el que ReportDateRange corta el rango.
        // Agrupar en el huso de la sesion de PostgreSQL pondria un pedido del 1 de enero en
        // diciembre para un tenant en America/Bogota, y ademas haria que el resultado dependiera
        // de una configuracion de conexion en vez del dato.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var monthRows = await joined
            .GroupBy(row => new
            {
                row.order.ConvertedAt.UtcDateTime.Year,
                row.order.ConvertedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
                Total = group.Sum(row => row.quotation.Total),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        var monthly = monthRows
            .Select(point => new ReportMonthlyPointDto(
                point.Year, point.Month, point.Count, point.Total))
            .ToArray();

        // Desempate por id en los dos rankings: sin un orden total, dos entidades con el mismo
        // monto pueden intercambiarse entre dos llamadas identicas y el ranking "parpadea".
        var topAdvisors = await joined
            .GroupBy(row => row.quotation.AdvisorId)
            .Select(group => new
            {
                AdvisorId = group.Key,
                Count = group.Count(),
                Total = group.Sum(row => row.quotation.Total),
            })
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.AdvisorId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var emails = await peopleLookup.EmailsByMembershipIdAsync(
            topAdvisors.Select(entry => entry.AdvisorId.Value).ToArray(), cancellationToken);

        var byAdvisor = topAdvisors
            .Select(entry => new ReportRankEntryDto(
                entry.AdvisorId.Value,
                emails.GetValueOrDefault(entry.AdvisorId.Value),
                Secondary: null,
                EntityCount: 1,
                entry.Count,
                entry.Total))
            .ToList();

        var otherAdvisors = await ReportRankFolding.FoldOthersAsync(
            byAdvisor, rankSize, totals.Count, totals.Total,
            () => joined
                .Select(row => row.quotation.AdvisorId)
                .Distinct()
                .CountAsync(cancellationToken));
        if (otherAdvisors is not null)
        {
            byAdvisor.Add(otherAdvisors);
        }

        var topClients = await joined
            .GroupBy(row => row.quotation.ClientId)
            .Select(group => new
            {
                ClientId = group.Key,
                Count = group.Count(),
                Total = group.Sum(row => row.quotation.Total),
            })
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.ClientId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var clients = await clientLookup.FindAsync(
            criteria.TenantId, topClients.Select(entry => entry.ClientId).ToArray(),
            cancellationToken);

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
                    entry.Total);
            })
            .ToList();

        var otherClients = await ReportRankFolding.FoldOthersAsync(
            byClient, rankSize, totals.Count, totals.Total,
            () => joined
                .Select(row => row.quotation.ClientId)
                .Distinct()
                .CountAsync(cancellationToken));
        if (otherClients is not null)
        {
            byClient.Add(otherClients);
        }

        return new OrdersReportAggregate(
            totals.Count, totals.Subtotal, totals.TaxAmount, totals.Total,
            monthly, byAdvisor, byClient);
    }

    /// <summary>
    /// Los pedidos filtrados por lo que es propio del pedido: tenant, rango de conversion y
    /// estado de pago.
    ///
    /// Separado de <see cref="FilterQuotations"/> porque los filtros del criterio caen sobre dos
    /// tablas distintas, y porque el join tiene que armarse **dentro** de cada metodo que
    /// consulta: EF no traduce un agregado sobre una proyeccion a un record —no ve a traves del
    /// constructor y falla con "could not be translated"—, asi que el resumen agrupa sobre el
    /// tipo anonimo del join y no sobre <see cref="OrderRow"/>.
    /// </summary>
    private IQueryable<Order> FilterOrders(OrdersReportCriteria criteria)
    {
        var orders = quotations.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == criteria.TenantId);

        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            orders = orders.Where(order => order.ConvertedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            orders = orders.Where(order => order.ConvertedAt < end);
        }

        if (criteria.PaymentStatus is { } paymentStatus)
        {
            var mapped = MapPaymentStatus(paymentStatus);
            orders = orders.Where(order => order.PaymentStatus == mapped);
        }

        return orders;
    }

    /// <summary>
    /// Las cotizaciones filtradas por lo que es propio de la cotizacion: asesor y cliente.
    ///
    /// **Sin filtro de tenant a proposito.** Lo aporta el join contra
    /// <see cref="FilterOrders"/>, que si lo tiene: una cotizacion solo entra si hay un pedido de
    /// este tenant que la apunte.
    /// </summary>
    private IQueryable<Quotation> FilterQuotations(OrdersReportCriteria criteria)
    {
        var candidates = quotations.Quotations.AsNoTracking();

        if (criteria.AdvisorId is { } advisorId)
        {
            var advisor = new MemberId(advisorId);
            candidates = candidates.Where(quotation => quotation.AdvisorId == advisor);
        }

        if (criteria.ClientId is { } clientId)
        {
            candidates = candidates.Where(quotation => quotation.ClientId == clientId);
        }

        return candidates;
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
        var joined = from order in FilterOrders(criteria)
                     join quotation in FilterQuotations(criteria)
                         on order.QuotationId equals quotation.Id
                     select new { order, quotation };

        return joined
            .OrderByDescending(row => row.order.ConvertedAt)
            .ThenBy(row => row.order.OrderNumber)
            .Select(row => new OrderRow(
                row.order.Id,
                row.order.OrderNumber,
                row.quotation.Id,
                row.quotation.QuotationNumber,
                row.order.ConvertedAt,
                row.quotation.AdvisorId,
                row.quotation.ClientId,
                row.order.Status,
                row.order.PaymentStatus,
                row.quotation.Subtotal,
                row.quotation.TaxAmount,
                row.quotation.Total));
    }

    // Dos consultas para toda la pagina, no dos por fila.
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

        return rows
            .Select(row =>
            {
                clients.TryGetValue(row.ClientId, out var client);
                return new OrdersReportItemDto(
                    row.OrderId.Value,
                    row.OrderNumber,
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
    private static OrderPaymentStatus MapPaymentStatus(OrderPaymentStatusFilter value) => value switch
    {
        OrderPaymentStatusFilter.FullPaymentReceived => OrderPaymentStatus.FullPaymentReceived,
        OrderPaymentStatusFilter.PartialPaymentReceived => OrderPaymentStatus.PartialPaymentReceived,
        OrderPaymentStatusFilter.PaymentPending => OrderPaymentStatus.PaymentPending,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown payment status.")
    };

    /// <summary>La fila cruda del join, con los tipos del dominio de <c>quotations</c>: los
    /// <c>.Value</c> de los identificadores se desenvuelven ya en memoria, porque EF no traduce
    /// el acceso a un miembro de una propiedad con conversor de valor.</summary>
    private sealed record OrderRow(
        OrderId OrderId,
        string OrderNumber,
        QuotationId QuotationId,
        string QuotationNumber,
        DateTimeOffset ConvertedAt,
        MemberId AdvisorId,
        Guid ClientId,
        OrderStatus Status,
        OrderPaymentStatus PaymentStatus,
        decimal Subtotal,
        decimal TaxAmount,
        decimal Total);
}
