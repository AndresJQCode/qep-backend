using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;
using Modules.Tenancy.Application;
using Npgsql;
using NpgsqlTypes;
using PosSaleStatus = Modules.Pos.Domain.PosSaleStatus;

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
///
/// **Desde el spec 2026-10-23 también lee <c>pos.sales</c>**: una venta POS es una fila más del
/// reporte, con origen <c>Pos</c>. Las dos tablas son de <c>DbContext</c> distintos
/// (<c>QuotationsDbContext</c> y <c>PosDbContext</c>) y EF no une dos contextos, así que la base del
/// reporte es SQL crudo con un <c>UNION ALL</c> (<see cref="ReportRowsSql"/>) que lee
/// <c>quotations.orders</c> y <c>pos.sales</c> **por nombre de tabla y de columna**. Renombrar una
/// columna de cualquiera de las dos compila y deja <c>/reports/orders</c> respondiendo 500: lo
/// amarran <c>OrdersReportPosApiTests</c> y el resto de las pruebas <c>OrdersReport*</c>, que
/// corren contra el esquema migrado. Paginar o rankear la unión en memoria está prohibido por la
/// misma razón por la que existe el resumen.
/// </summary>
internal sealed class OrdersReportSource(
    QuotationsDbContext quotations,
    ReportingClientLookup clientLookup,
    ReportingPeopleLookup peopleLookup,
    ITenantDefaultCurrency tenantDefaultCurrency) : IOrdersReportSource
{
    /// <summary>
    /// Orden explícito y total sobre las dos fuentes: sin él, dos páginas consecutivas pueden
    /// repetir u omitir filas, porque PostgreSQL no garantiza ningún orden sin <c>ORDER BY</c>. Lo
    /// más nuevo primero, que es como se lee un reporte de pedidos; después el número, y el id como
    /// último desempate, porque <c>POS-…</c> y el número de pedido no son únicos entre ellos.
    /// </summary>
    public async Task<(IReadOnlyList<OrdersReportItemDto> Items, int Total)> ListAsync(
        OrdersReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var total = await CountAsync(criteria, """SELECT count(*)::int AS "Value" FROM r""", cancellationToken);

        var rows = await QueryAsync<ReportRow>(
            criteria,
            """
            SELECT r.id AS "Id", r.number AS "Number", r.source AS "Source", r.quotation_id AS "QuotationId",
                   r.occurred_at AS "OccurredAt", r.advisor_id AS "AdvisorId", r.client_id AS "ClientId",
                   r.status AS "Status", r.payment_status AS "PaymentStatus", r.currency AS "Currency",
                   r.subtotal AS "Subtotal", r.tax_amount AS "TaxAmount", r.freight_subtotal AS "FreightSubtotal",
                   r.freight_tax_amount AS "FreightTaxAmount", r.total AS "Total", r.is_void AS "IsVoid",
                   r.snapshot_client_name AS "SnapshotClientName"
            FROM r
            ORDER BY r.occurred_at DESC, r.number, r.id
            LIMIT @page_limit OFFSET @page_offset
            """,
            [
                new NpgsqlParameter("page_limit", NpgsqlDbType.Integer) { Value = pageSize },
                new NpgsqlParameter("page_offset", NpgsqlDbType.Integer) { Value = (page - 1) * pageSize },
            ],
            cancellationToken);

        return (await ToDtosAsync(criteria.TenantId, rows, cancellationToken), total);
    }

    /// <summary>
    /// Corre <paramref name="query"/> sobre la base de <see cref="ReportRowsSql"/>, que queda
    /// disponible como la CTE <c>r</c>: cada consulta se lee entera, <c>SELECT … FROM r …</c>. Desde
    /// PostgreSQL 12 una CTE que se usa una sola vez se integra a la consulta, así que los filtros de
    /// cada rama siguen usando sus índices.
    ///
    /// Van los parámetros de la base, creados de nuevo para este comando, más los propios de la
    /// consulta. La consulta es una constante del llamador: el texto nunca lleva un valor.
    /// </summary>
    private Task<List<T>> QueryAsync<T>(
        OrdersReportCriteria criteria,
        string query,
        IReadOnlyList<NpgsqlParameter> extraParameters,
        CancellationToken cancellationToken)
    {
        var (baseSql, parameters) = ReportRowsSql(criteria);
        var sql = string.Concat("WITH r AS (\n", baseSql, "\n)\n", query);
        object[] arguments = [.. parameters, .. extraParameters];
        return quotations.Database.SqlQueryRaw<T>(sql, arguments).ToListAsync(cancellationToken);
    }

    /// <summary>Un conteo escalar sobre la base: <paramref name="query"/> devuelve una sola
    /// columna <c>"Value"</c>, que es como EF mapea un <c>SqlQuery</c> de un tipo simple.</summary>
    private async Task<int> CountAsync(
        OrdersReportCriteria criteria,
        string query,
        CancellationToken cancellationToken)
    {
        var counted = await QueryAsync<int>(criteria, query, [], cancellationToken);
        return counted.Count == 0 ? 0 : counted[0];
    }

    /// <summary>
    /// Todo se agrega **en la base**: ningun camino de aca trae filas para sumarlas en memoria,
    /// que es justo lo que el resumen existe para evitar.
    ///
    /// Cada agregado es una consulta sobre la base de <see cref="ReportRowsSql"/> —pedidos y ventas
    /// POS, la CTE <c>r</c>— con <c>WHERE NOT r.is_void</c>. Son varias consultas y no una porque son agregaciones de
    /// distinta granularidad —los totales por moneda, la serie por mes, el origen, lo que no tiene
    /// cliente y dos rankings—, y cada rama de la base ataca su propio índice.
    /// </summary>
    public async Task<OrdersReportAggregate> SummarizeAsync(
        OrdersReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        // Lo anulado no es una venta (spec 2026-09-16, decisión 6; spec 2026-10-23 §3): un pedido
        // cancelado y una venta POS anulada no suman en el total, la serie, los rankings ni el
        // origen, y tampoco en el período anterior, que pasa por este mismo método. El listado no
        // lo filtra: ahí lo anulado se ve con su estado.
        //
        // Una fila por moneda: COP y USD nunca se suman (spec 2026-10-08, Reportes). Sin filas, el
        // conteo de abajo da cero.
        var byCurrency = await QueryAsync<MoneyRow>(
            criteria,
            """
            SELECT r.currency AS "Currency", count(*)::int AS "Count", sum(r.subtotal) AS "Subtotal",
                   sum(r.tax_amount) AS "TaxAmount", sum(r.freight_subtotal) AS "FreightSubtotal",
                   sum(r.freight_tax_amount) AS "FreightTaxAmount", sum(r.total) AS "Total"
            FROM r
            WHERE NOT r.is_void
            GROUP BY r.currency
            """,
            [],
            cancellationToken);

        // Sin filas no hay nada que agrupar ni ninguna etiqueta que resolver: varias consultas
        // menos. Las colecciones de tamaño fijo viajan completas igual, en cero.
        var orderCount = byCurrency.Sum(row => row.Count);
        if (orderCount == 0)
        {
            return new OrdersReportAggregate(
                0, [], [], [], [], [], [], [], [], CompleteBySource([]), new ReportWithoutClientDto(0, []));
        }

        var subtotals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Subtotal)));
        var taxAmounts = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.TaxAmount)));
        var freightSubtotals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.FreightSubtotal)));
        var freightTaxAmounts = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.FreightTaxAmount)));
        var totals = ReportMoney.From(byCurrency.Select(row => (row.Currency, row.Total)));

        var sourceRows = await QueryAsync<SourceRow>(
            criteria,
            """
            SELECT r.source AS "Source", r.currency AS "Currency", count(*)::int AS "Count", sum(r.total) AS "Total"
            FROM r
            WHERE NOT r.is_void
            GROUP BY r.source, r.currency
            """,
            [],
            cancellationToken);
        var bySource = CompleteBySource(sourceRows
            .Select(row => (row.Source, row.Currency, row.Count, row.Total))
            .ToArray());

        // Las ventas POS a consumidor final no tienen ficha de cliente: van aparte y no al ranking
        // (ver ReportWithoutClientDto). Un pedido siempre tiene cliente.
        var withoutClientRows = await QueryAsync<CurrencyCountRow>(
            criteria,
            """
            SELECT r.currency AS "Currency", count(*)::int AS "Count", sum(r.total) AS "Total"
            FROM r
            WHERE NOT r.is_void AND r.client_id IS NULL
            GROUP BY r.currency
            """,
            [],
            cancellationToken);
        var withoutClient = new ReportWithoutClientDto(
            withoutClientRows.Sum(row => row.Count),
            ReportMoney.From(withoutClientRows.Select(row => (row.Currency, row.Total))));

        // La serie mensual va en el mes del tenant (spec 2026-09-17, punto 5): el pedido o la venta
        // de las 20:00 del último día en Bogotá es de ese mes, aunque en UTC ya sea el siguiente. Se
        // agrupa en SQL con `AT TIME ZONE` y el ID IANA del tenant, nunca en el huso de la sesión de
        // PostgreSQL, que haría depender el resultado de la conexión.
        //
        // Solo vuelven los meses con filas: rellenar los huecos con cero depende del rango que el eje
        // dibuje, asi que es del frontend.
        var monthRows = await QueryAsync<MonthRow>(
            criteria,
            """
            SELECT extract(year FROM r.occurred_at AT TIME ZONE @time_zone)::int AS "Year",
                   extract(month FROM r.occurred_at AT TIME ZONE @time_zone)::int AS "Month",
                   r.currency AS "Currency", count(*)::int AS "Count", sum(r.total) AS "Total"
            FROM r
            WHERE NOT r.is_void
            GROUP BY 1, 2, 3
            """,
            [new NpgsqlParameter("time_zone", NpgsqlDbType.Text) { Value = criteria.Period.TimeZone.Id }],
            cancellationToken);

        // Se pliega en memoria: un punto por mes, con sus totales por moneda.
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

        var defaultCurrency = await tenantDefaultCurrency.GetAsync(criteria.TenantId, cancellationToken);

        // El cajero de una venta entra como un asesor más, por su membresía (spec 2026-10-23 §3).
        var advisorRanking = await RankAsync(
            criteria, RankedBy.Advisor, rankSize, defaultCurrency, cancellationToken);
        var emails = await peopleLookup.EmailsByMembershipIdAsync(
            advisorRanking.Select(entry => entry.Id).ToArray(), cancellationToken);
        var byAdvisor = advisorRanking
            .Select(entry => new ReportRankEntryDto(
                entry.Id, emails.GetValueOrDefault(entry.Id), Secondary: null, EntityCount: 1, entry.Count, entry.Totals))
            .ToList();
        var otherAdvisors = await ReportRankFolding.FoldOthersAsync(
            byAdvisor, rankSize, orderCount, totals,
            () => CountDistinctAsync(criteria, RankedBy.Advisor, cancellationToken));
        if (otherAdvisors is not null)
        {
            byAdvisor.Add(otherAdvisors);
        }

        var clientRanking = await RankAsync(
            criteria, RankedBy.Client, rankSize, defaultCurrency, cancellationToken);
        var clients = await clientLookup.FindAsync(
            criteria.TenantId, clientRanking.Select(entry => entry.Id).ToArray(), cancellationToken);
        var byClient = clientRanking
            .Select(entry =>
            {
                clients.TryGetValue(entry.Id, out var client);
                return new ReportRankEntryDto(
                    entry.Id, client?.Name, client?.Cuc, EntityCount: 1, entry.Count, entry.Totals);
            })
            .ToList();

        // «Otros» de clientes es el resto **sin** lo que no tiene cliente: con lo de mostrador
        // adentro, el pliegue contaría ventas que no son de ningún cliente, y byClient +
        // withoutClient dejaría de cerrar contra el total.
        var otherClients = await ReportRankFolding.FoldOthersAsync(
            byClient,
            rankSize,
            orderCount - withoutClient.Count,
            ReportMoney.Remainder(totals, [withoutClient.Totals]),
            () => CountDistinctAsync(criteria, RankedBy.Client, cancellationToken));
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
            withoutClient);
    }

    /// <summary>Por qué se rankea. Un tipo cerrado y no el nombre de la columna: la columna se
    /// escribe en el SQL, así que ningún llamador puede pasar texto libre.</summary>
    private enum RankedBy
    {
        Advisor,
        Client,
    }

    // La columna de la base de cada ranking. Sin default: un valor nuevo rompe acá.
    private static string ColumnOf(RankedBy rankedBy) => rankedBy switch
    {
        RankedBy.Advisor => "advisor_id",
        RankedBy.Client => "client_id",
        _ => throw new ArgumentOutOfRangeException(nameof(rankedBy), rankedBy, "Unknown ranking."),
    };

    /// <summary>
    /// El top <paramref name="rankSize"/> por asesor o por cliente, con sus totales por moneda.
    ///
    /// Se ordena por el total en la moneda por defecto del tenant (decisión A10 del plan): un tenant
    /// de una sola moneda rankea igual que antes, y uno mixto tiene un orden determinista que nunca
    /// suma dos monedas. El conteo y el id desempatan, para que el ranking no «parpadee» entre dos
    /// llamadas idénticas. Las filas sin valor en la columna (una venta a consumidor final, en
    /// clientes) no entran.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, int Count, IReadOnlyList<ReportMoneyDto> Totals)>> RankAsync(
        OrdersReportCriteria criteria,
        RankedBy rankedBy,
        int rankSize,
        string defaultCurrency,
        CancellationToken cancellationToken)
    {
        var column = ColumnOf(rankedBy);
        var top = await QueryAsync<RankRow>(
            criteria,
            $"""
            SELECT r.{column} AS "Id", count(*)::int AS "Count",
                   sum(CASE WHEN r.currency = @default_currency THEN r.total ELSE 0 END) AS "Ranking"
            FROM r
            WHERE NOT r.is_void AND r.{column} IS NOT NULL
            GROUP BY r.{column}
            ORDER BY "Ranking" DESC, "Count" DESC, "Id"
            LIMIT @rank_size
            """,
            [
                new NpgsqlParameter("default_currency", NpgsqlDbType.Text) { Value = defaultCurrency },
                new NpgsqlParameter("rank_size", NpgsqlDbType.Integer) { Value = rankSize },
            ],
            cancellationToken);
        if (top.Count == 0)
        {
            return [];
        }

        var ids = top.Select(entry => entry.Id).ToArray();
        var entityTotals = await QueryAsync<EntityTotalRow>(
            criteria,
            $"""
            SELECT r.{column} AS "Id", r.currency AS "Currency", sum(r.total) AS "Total"
            FROM r
            WHERE NOT r.is_void AND r.{column} = ANY(@ranked_ids)
            GROUP BY r.{column}, r.currency
            """,
            [new NpgsqlParameter("ranked_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids }],
            cancellationToken);

        return top
            .Select(entry => (
                entry.Id,
                entry.Count,
                ReportMoney.From(entityTotals
                    .Where(total => total.Id == entry.Id)
                    .Select(total => (total.Currency, total.Total)))))
            .ToArray();
    }

    /// <summary>Cuántos asesores o clientes distintos hay, sin los nulos: el conteo del pliegue
    /// «Otros».</summary>
    private Task<int> CountDistinctAsync(
        OrdersReportCriteria criteria,
        RankedBy rankedBy,
        CancellationToken cancellationToken) =>
        CountAsync(
            criteria,
            $"""SELECT count(DISTINCT r.{ColumnOf(rankedBy)})::int AS "Value" FROM r WHERE NOT r.is_void""",
            cancellationToken);

    /// <summary>
    /// La base del reporte: un <c>UNION ALL</c> de <c>quotations.orders</c> y <c>pos.sales</c> ya
    /// filtrado por el criterio, con las mismas columnas en las dos ramas (ver
    /// <see cref="ReportRow"/>). El listado la ordena y la pagina, y cada agregado del resumen la
    /// agrupa: todo en la base.
    ///
    /// <para>
    /// **Cada rama lleva todos los filtros en su propio <c>WHERE</c>**, para que sirvan
    /// <c>IX_sales_tenant_created</c> y los índices de pedidos. Una rama que el criterio no puede
    /// traer se omite entera: <c>source</c> distinto de <c>Pos</c> omite las ventas, <c>Pos</c>
    /// omite los pedidos, y un estado de pago distinto de <c>FullPaymentReceived</c> omite las ventas
    /// (una venta POS siempre está pagada). Sin ninguna rama queda un <c>SELECT</c> vacío con las
    /// mismas columnas.
    /// </para>
    /// <para>
    /// El SQL se compone **sólo con fragmentos constantes**: ningún valor se interpola en el texto.
    /// Un filtro ausente no se escribe, en vez de mandarse como parámetro nulo —PostgreSQL no sabe
    /// tipar un <c>NULL</c> suelto (<c>could not determine data type of parameter</c>)—, y los
    /// presentes van como <see cref="NpgsqlParameter"/> con tipo explícito. Los parámetros se crean
    /// de nuevo en cada llamada: Npgsql no deja que uno pertenezca a dos comandos.
    /// </para>
    /// </summary>
    private static (string Sql, NpgsqlParameter[] Parameters) ReportRowsSql(OrdersReportCriteria criteria)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("tenant_id", NpgsqlDbType.Uuid) { Value = criteria.TenantId },
            new("order_cancelled", NpgsqlDbType.Text) { Value = OrderStatus.Cancelled.ToString() },
            new("sale_voided", NpgsqlDbType.Text) { Value = PosSaleStatus.Voided.ToString() },
            new("sale_source", NpgsqlDbType.Text) { Value = OrderSourceFilter.Pos.ToString() },
            new("sale_payment_status", NpgsqlDbType.Text) { Value = OrderPaymentStatus.FullPaymentReceived.ToString() },
        };
        var orderFilters = new StringBuilder();
        var saleFilters = new StringBuilder();

        // Instantes ya cortados en el día del tenant (spec 2026-09-17, punto 4): acá no se decide huso.
        if (criteria.Period.Start is { } start)
        {
            parameters.Add(new NpgsqlParameter("period_start", NpgsqlDbType.TimestampTz) { Value = start.UtcDateTime });
            orderFilters.Append(" AND o.converted_at >= @period_start");
            saleFilters.Append(" AND s.created_at >= @period_start");
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            parameters.Add(new NpgsqlParameter("period_end", NpgsqlDbType.TimestampTz) { Value = end.UtcDateTime });
            orderFilters.Append(" AND o.converted_at < @period_end");
            saleFilters.Append(" AND s.created_at < @period_end");
        }

        // El asesor del pedido y el cajero de la venta son el mismo tipo de id, una membresía: el
        // alcance que fija ScopeAdvisorAsync vale igual para los dos (spec 2026-10-23 §4).
        if (criteria.AdvisorId is { } advisorId)
        {
            parameters.Add(new NpgsqlParameter("advisor_id", NpgsqlDbType.Uuid) { Value = advisorId });
            orderFilters.Append(" AND o.advisor_id = @advisor_id");
            saleFilters.Append(" AND s.cashier_id = @advisor_id");
        }

        // Una venta a consumidor final no tiene customer_id: nunca coincide con un cliente.
        if (criteria.ClientId is { } clientId)
        {
            parameters.Add(new NpgsqlParameter("client_id", NpgsqlDbType.Uuid) { Value = clientId });
            orderFilters.Append(" AND o.client_id = @client_id");
            saleFilters.Append(" AND s.customer_id = @client_id");
        }

        var includeOrders = criteria.Source is not OrderSourceFilter.Pos;
        var includeSales = criteria.Source is null or OrderSourceFilter.Pos;

        if (criteria.Source is { } source && MapSource(source) is { } orderSource)
        {
            parameters.Add(new NpgsqlParameter("order_source", NpgsqlDbType.Text) { Value = orderSource.ToString() });
            orderFilters.Append(" AND o.source = @order_source");
        }

        if (criteria.PaymentStatus is { } paymentStatus)
        {
            var mapped = MapPaymentStatus(paymentStatus);
            parameters.Add(new NpgsqlParameter("payment_status", NpgsqlDbType.Text) { Value = mapped.ToString() });
            orderFilters.Append(" AND o.payment_status = @payment_status");
            includeSales &= mapped == OrderPaymentStatus.FullPaymentReceived;
        }

        var branches = new List<string>(2);
        if (includeOrders)
        {
            branches.Add(OrdersBranch + orderFilters);
        }

        if (includeSales)
        {
            branches.Add(SalesBranch + saleFilters);
        }

        var sql = branches.Count == 0 ? EmptyBranch : string.Join("\nUNION ALL\n", branches);
        return (sql, [.. parameters]);
    }

    // Las columnas de las dos ramas, en el mismo orden y con el mismo tipo: UNION ALL las empareja
    // por posición. Los textos van a ::text para que la unión no dependa del largo de cada varchar.
    private const string OrdersBranch = """
        SELECT o.id AS id, o.order_number::text AS number, o.source::text AS source,
               o.quotation_id AS quotation_id, o.converted_at AS occurred_at, o.advisor_id AS advisor_id,
               o.client_id AS client_id, o.status::text AS status, o.payment_status::text AS payment_status,
               o.currency::text AS currency, o.subtotal AS subtotal, o.tax_amount AS tax_amount,
               o.freight_subtotal AS freight_subtotal, o.freight_tax_amount AS freight_tax_amount,
               o.total AS total, (o.status = @order_cancelled) AS is_void, NULL::text AS snapshot_client_name
        FROM quotations.orders AS o
        WHERE o.tenant_id = @tenant_id
        """;

    private const string SalesBranch = """
        SELECT s.id AS id, s.sale_number::text AS number, @sale_source::text AS source,
               NULL::uuid AS quotation_id, s.created_at AS occurred_at, s.cashier_id AS advisor_id,
               s.customer_id AS client_id, s.status::text AS status, @sale_payment_status::text AS payment_status,
               s.currency::text AS currency, s.subtotal AS subtotal, s.tax_amount AS tax_amount,
               0::numeric(14, 2) AS freight_subtotal, 0::numeric(14, 2) AS freight_tax_amount,
               s.total AS total, (s.status = @sale_voided) AS is_void, s.customer_name::text AS snapshot_client_name
        FROM pos.sales AS s
        WHERE s.tenant_id = @tenant_id
        """;

    private const string EmptyBranch = """
        SELECT NULL::uuid AS id, NULL::text AS number, NULL::text AS source, NULL::uuid AS quotation_id,
               NULL::timestamptz AS occurred_at, NULL::uuid AS advisor_id, NULL::uuid AS client_id,
               NULL::text AS status, NULL::text AS payment_status, NULL::text AS currency,
               NULL::numeric(14, 2) AS subtotal, NULL::numeric(14, 2) AS tax_amount,
               NULL::numeric(14, 2) AS freight_subtotal, NULL::numeric(14, 2) AS freight_tax_amount,
               NULL::numeric(14, 2) AS total, NULL::boolean AS is_void, NULL::text AS snapshot_client_name
        WHERE false
        """;

    // Tres consultas para toda la pagina, no tres por fila.
    private async Task<IReadOnlyList<OrdersReportItemDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<ReportRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var advisors = await peopleLookup.EmailsByMembershipIdAsync(
            rows.Select(row => row.AdvisorId).ToArray(), cancellationToken);
        var clients = await clientLookup.FindAsync(
            tenantId,
            rows.Where(row => row.ClientId is not null).Select(row => row.ClientId!.Value).ToArray(),
            cancellationToken);
        var quotationIds = rows
            .Where(row => row.QuotationId is not null)
            .Select(row => new QuotationId(row.QuotationId!.Value))
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
                // Una venta a consumidor final no tiene ficha: el nombre es el que guardó la venta
                // («Consumidor final») y el CUC queda nulo.
                ReportingClientRef? client = null;
                if (row.ClientId is { } clientId)
                {
                    clients.TryGetValue(clientId, out client);
                }

                return new OrdersReportItemDto(
                    row.Id,
                    row.Number,
                    row.Source,
                    row.QuotationId,
                    row.QuotationId is { } quotationId
                        ? quotationNumbers.GetValueOrDefault(new QuotationId(quotationId))
                        : null,
                    row.OccurredAt,
                    row.AdvisorId,
                    advisors.GetValueOrDefault(row.AdvisorId),
                    row.ClientId,
                    row.ClientId is null ? row.SnapshotClientName : client?.Name,
                    client?.Cuc,
                    row.Status,
                    row.PaymentStatus,
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

    /// <summary>
    /// Una fila de la base, pedido o venta POS, con los alias de <see cref="ReportRowsSql"/> en
    /// PascalCase. Clase con <c>init</c> y no un record: EF no ve a través del constructor de un
    /// record en <c>SqlQuery</c> (precedente <c>PipelineConversationDirectory</c>).
    /// </summary>
    private sealed class ReportRow
    {
        public Guid Id { get; init; }

        public string Number { get; init; } = string.Empty;

        public string Source { get; init; } = string.Empty;

        public Guid? QuotationId { get; init; }

        public DateTimeOffset OccurredAt { get; init; }

        public Guid AdvisorId { get; init; }

        /// <summary>Nulo sólo en una venta POS a consumidor final.</summary>
        public Guid? ClientId { get; init; }

        public string Status { get; init; } = string.Empty;

        public string PaymentStatus { get; init; } = string.Empty;

        public string Currency { get; init; } = string.Empty;

        public decimal Subtotal { get; init; }

        public decimal TaxAmount { get; init; }

        public decimal FreightSubtotal { get; init; }

        public decimal FreightTaxAmount { get; init; }

        public decimal Total { get; init; }

        public bool IsVoid { get; init; }

        /// <summary>El nombre que guardó la venta POS; nulo en los pedidos.</summary>
        public string? SnapshotClientName { get; init; }
    }

    private sealed class MoneyRow
    {
        public string Currency { get; init; } = string.Empty;

        public int Count { get; init; }

        public decimal Subtotal { get; init; }

        public decimal TaxAmount { get; init; }

        public decimal FreightSubtotal { get; init; }

        public decimal FreightTaxAmount { get; init; }

        public decimal Total { get; init; }
    }

    private sealed class CurrencyCountRow
    {
        public string Currency { get; init; } = string.Empty;

        public int Count { get; init; }

        public decimal Total { get; init; }
    }

    private sealed class SourceRow
    {
        public string Source { get; init; } = string.Empty;

        public string Currency { get; init; } = string.Empty;

        public int Count { get; init; }

        public decimal Total { get; init; }
    }

    private sealed class MonthRow
    {
        public int Year { get; init; }

        public int Month { get; init; }

        public string Currency { get; init; } = string.Empty;

        public int Count { get; init; }

        public decimal Total { get; init; }
    }

    private sealed class RankRow
    {
        public Guid Id { get; init; }

        public int Count { get; init; }

        public decimal Ranking { get; init; }
    }

    private sealed class EntityTotalRow
    {
        public Guid Id { get; init; }

        public string Currency { get; init; } = string.Empty;

        public decimal Total { get; init; }
    }
}
