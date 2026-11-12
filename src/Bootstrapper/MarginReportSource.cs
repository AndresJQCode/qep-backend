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
/// Adapta las líneas de <c>quotations.order_items</c> y <c>pos.sale_lines</c>, y los nombres de
/// <c>catalog</c>, al puerto del reporte de margen (spec 2026-11-10 §6.3). Vive acá por la misma razón
/// que <see cref="OrdersReportSource"/>: Reporting no referencia a ningún otro módulo de negocio.
///
/// **Misma técnica que <see cref="OrdersReportSource"/>**: SQL crudo con un <c>UNION ALL</c> de las dos
/// tablas (<see cref="LinesSql"/>), compuesto sólo con fragmentos constantes y parámetros tipados, y
/// todo agregado en la base. Lee tablas y columnas por nombre: renombrar una compila y deja
/// <c>/reports/margin</c> respondiendo 500. Lo amarra <c>MarginReportApiTests</c>, que corre contra el
/// esquema migrado.
///
/// **No decide nada** salvo lo que es propio del dato: qué cuenta como vendido (pedido pagado y no
/// anulado, venta POS completada) y de dónde sale cada nombre. El porcentaje lo calcula
/// <see cref="ReportMargin"/>, que tiene prueba unitaria.
/// </summary>
internal sealed class MarginReportSource(
    QuotationsDbContext quotations,
    ITenantDefaultCurrency tenantDefaultCurrency) : IMarginReportSource
{
    /// <summary>
    /// Una fila por moneda, en orden de catálogo (C10). Las líneas sin costo no entran a venta neta,
    /// costo ni margen, y se cuentan aparte: una moneda en la que **ninguna** línea tiene costo vuelve
    /// igual, con el margen en cero y el porcentaje nulo, para que el aviso de lo que quedó fuera se vea.
    /// </summary>
    public async Task<IReadOnlyList<MarginReportCurrencyDto>> SummarizeAsync(
        MarginReportCriteria criteria,
        CancellationToken ct)
    {
        var rows = await QueryAsync<CurrencyRow>(
            criteria,
            """
            SELECT l.currency AS "Currency",
                   coalesce(sum(l.net_sales) FILTER (WHERE l.costed), 0) AS "NetSales",
                   coalesce(sum(l.cost) FILTER (WHERE l.costed), 0) AS "Cost",
                   (count(*) FILTER (WHERE NOT l.costed))::int AS "UncostedLines",
                   coalesce(sum(l.net_sales) FILTER (WHERE NOT l.costed), 0) AS "UncostedNetSales"
            FROM l
            GROUP BY l.currency
            """,
            [],
            ct);

        return rows
            .OrderBy(row => Currencies.OrderOf(row.Currency))
            .ThenBy(row => row.Currency, StringComparer.Ordinal)
            .Select(row => new MarginReportCurrencyDto(
                row.Currency,
                row.NetSales,
                row.Cost,
                row.NetSales - row.Cost,
                ReportMargin.Percent(row.NetSales, row.NetSales - row.Cost),
                row.UncostedLines,
                row.UncostedNetSales))
            .ToArray();
    }

    /// <summary>
    /// Una fila por producto y moneda, sólo con las líneas que tienen costo (C9). Orden C11: primero
    /// la moneda por defecto del tenant, después la utilidad descendente, y el producto y la moneda
    /// desempatan para que dos páginas seguidas no repitan ni salten filas. Las variantes de toda la
    /// página salen en **una** consulta más.
    /// </summary>
    public async Task<(IReadOnlyList<MarginReportProductDto> Items, int Total)> ListAsync(
        MarginReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var counted = await QueryAsync<int>(
            criteria, ProductGroupsSql + """SELECT count(*)::int AS "Value" FROM g""", [], ct);
        var total = counted.Count == 0 ? 0 : counted[0];
        if (total == 0)
        {
            return ([], 0);
        }

        var defaultCurrency = await tenantDefaultCurrency.GetAsync(criteria.TenantId, ct);
        var products = await QueryAsync<ProductRow>(
            criteria,
            ProductGroupsSql + """
            SELECT g.product_id AS "ProductId", g.currency AS "Currency", g.quantity AS "Quantity",
                   g.net_sales AS "NetSales", g.cost AS "Cost",
                   coalesce(p.name::text, latest.line_name, @unnamed) AS "ProductName"
            FROM g
            LEFT JOIN catalog.products AS p ON p.id = g.product_id AND p.tenant_id = @tenant_id
            LEFT JOIN LATERAL (
                SELECT named.line_name FROM l AS named
                WHERE named.product_id = g.product_id AND named.line_name IS NOT NULL
                ORDER BY named.sold_at DESC NULLS LAST
                LIMIT 1
            ) AS latest ON true
            ORDER BY (g.currency = @default_currency) DESC, (g.net_sales - g.cost) DESC, g.product_id, g.currency
            LIMIT @page_limit OFFSET @page_offset
            """,
            [
                new NpgsqlParameter("unnamed", NpgsqlDbType.Text) { Value = UnnamedProduct },
                new NpgsqlParameter("default_currency", NpgsqlDbType.Text) { Value = defaultCurrency },
                new NpgsqlParameter("page_limit", NpgsqlDbType.Integer) { Value = pageSize },
                new NpgsqlParameter("page_offset", NpgsqlDbType.Integer) { Value = (page - 1) * pageSize },
            ],
            ct);
        if (products.Count == 0)
        {
            return ([], total);
        }

        var variants = await QueryAsync<VariantRow>(
            criteria,
            """
            , v AS (
                SELECT l.product_id, l.currency, l.variant_id, sum(l.quantity) AS quantity,
                       sum(l.net_sales) AS net_sales, sum(l.cost) AS cost
                FROM l
                JOIN unnest(@page_products, @page_currencies) AS page(product_id, currency)
                  ON page.product_id = l.product_id AND page.currency = l.currency
                WHERE l.costed
                GROUP BY l.product_id, l.currency, l.variant_id
            )
            SELECT v.product_id AS "ProductId", v.currency AS "Currency", v.variant_id AS "VariantId",
                   v.quantity AS "Quantity", v.net_sales AS "NetSales", v.cost AS "Cost",
                   coalesce(named.line_name, pv.code::text, coded.line_code, @unnamed) AS "VariantName"
            FROM v
            LEFT JOIN catalog.product_variants AS pv ON pv.id = v.variant_id AND pv.tenant_id = @tenant_id
            LEFT JOIN LATERAL (
                SELECT x.line_name FROM l AS x
                WHERE x.variant_id = v.variant_id AND x.line_name IS NOT NULL
                ORDER BY x.sold_at DESC NULLS LAST
                LIMIT 1
            ) AS named ON true
            LEFT JOIN LATERAL (
                SELECT x.line_code FROM l AS x
                WHERE x.variant_id = v.variant_id AND x.line_code IS NOT NULL
                ORDER BY x.sold_at DESC NULLS LAST
                LIMIT 1
            ) AS coded ON true
            ORDER BY (v.net_sales - v.cost) DESC, v.variant_id
            """,
            [
                new NpgsqlParameter("page_products", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = products.Select(row => row.ProductId).ToArray(),
                },
                new NpgsqlParameter("page_currencies", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = products.Select(row => row.Currency).ToArray(),
                },
                new NpgsqlParameter("unnamed", NpgsqlDbType.Text) { Value = UnnamedProduct },
            ],
            ct);

        var items = products
            .Select(product => new MarginReportProductDto(
                product.ProductId,
                product.ProductName,
                product.Currency,
                product.Quantity,
                product.NetSales,
                product.Cost,
                product.NetSales - product.Cost,
                ReportMargin.Percent(product.NetSales, product.NetSales - product.Cost),
                variants
                    .Where(variant => variant.ProductId == product.ProductId && variant.Currency == product.Currency)
                    .Select(variant => new MarginReportVariantDto(
                        variant.VariantId,
                        variant.VariantName,
                        variant.Quantity,
                        variant.NetSales,
                        variant.Cost,
                        variant.NetSales - variant.Cost,
                        ReportMargin.Percent(variant.NetSales, variant.NetSales - variant.Cost)))
                    .ToArray()))
            .ToArray();

        return (items, total);
    }

    /// <summary>Lo que se muestra cuando ni el catálogo ni ninguna línea guardaron un nombre.</summary>
    private const string UnnamedProduct = "Producto sin nombre";

    /// <summary>Los productos con costo agrupados por moneda, como CTE <c>g</c> detrás de <c>l</c>. La
    /// comparten el conteo y la página, que así no pueden hablar de grupos distintos.</summary>
    private const string ProductGroupsSql = """
        , g AS (
            SELECT l.product_id, l.currency, sum(l.quantity) AS quantity, sum(l.net_sales) AS net_sales,
                   sum(l.cost) AS cost
            FROM l
            WHERE l.costed
            GROUP BY l.product_id, l.currency
        )

        """;

    /// <summary>
    /// Corre <paramref name="query"/> con las líneas de <see cref="LinesSql"/> disponibles como la CTE
    /// <c>l</c>. La consulta empieza con <c>SELECT</c> o con <c>, otra AS (…)</c> para sumar CTEs.
    /// La consulta es una constante del llamador: el texto nunca lleva un valor.
    /// </summary>
    private Task<List<T>> QueryAsync<T>(
        MarginReportCriteria criteria,
        string query,
        IReadOnlyList<NpgsqlParameter> extraParameters,
        CancellationToken ct)
    {
        var (linesSql, parameters) = LinesSql(criteria);
        var sql = string.Concat("WITH l AS (\n", linesSql, "\n)\n", query);
        object[] arguments = [.. parameters, .. extraParameters];
        return quotations.Database.SqlQueryRaw<T>(sql, arguments).ToListAsync(ct);
    }

    /// <summary>
    /// Una fila por línea vendida: las de pedidos pagados y no anulados, con el período sobre
    /// <c>paid_at</c> (el último momento en que quedó pagado, spec §4.4), y las de ventas POS
    /// completadas, con el período sobre <c>created_at</c>.
    ///
    /// Cada rama lleva todos sus filtros en su propio <c>WHERE</c>, para que sirvan
    /// <c>IX_orders_tenant_paid_at</c> e <c>IX_sales_tenant_created</c>. Un <c>source</c> distinto de
    /// <c>Pos</c> omite las ventas y <c>Pos</c> omite los pedidos; nunca quedan las dos fuera. Un filtro
    /// ausente no se escribe, en vez de mandarse como parámetro nulo que PostgreSQL no sabe tipar.
    /// </summary>
    private static (string Sql, NpgsqlParameter[] Parameters) LinesSql(MarginReportCriteria criteria)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("tenant_id", NpgsqlDbType.Uuid) { Value = criteria.TenantId },
        };
        var orderFilters = new StringBuilder();
        var saleFilters = new StringBuilder();

        if (criteria.Period.Start is { } start)
        {
            parameters.Add(new NpgsqlParameter("period_start", NpgsqlDbType.TimestampTz) { Value = start.UtcDateTime });
            orderFilters.Append(" AND o.paid_at >= @period_start");
            saleFilters.Append(" AND s.created_at >= @period_start");
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            parameters.Add(new NpgsqlParameter("period_end", NpgsqlDbType.TimestampTz) { Value = end.UtcDateTime });
            orderFilters.Append(" AND o.paid_at < @period_end");
            saleFilters.Append(" AND s.created_at < @period_end");
        }

        var includeOrders = criteria.Source is not OrderSourceFilter.Pos;
        var includeSales = criteria.Source is null or OrderSourceFilter.Pos;

        if (criteria.Source is { } source && MapSource(source) is { } orderSource)
        {
            parameters.Add(new NpgsqlParameter("order_source", NpgsqlDbType.Text) { Value = orderSource.ToString() });
            orderFilters.Append(" AND o.source = @order_source");
        }

        var branches = new List<string>(2);
        if (includeOrders)
        {
            parameters.Add(new NpgsqlParameter("order_paid", NpgsqlDbType.Text)
            {
                Value = OrderPaymentStatus.FullPaymentReceived.ToString(),
            });
            parameters.Add(new NpgsqlParameter("order_cancelled", NpgsqlDbType.Text)
            {
                Value = OrderStatus.Cancelled.ToString(),
            });
            branches.Add(OrdersBranch + orderFilters);
        }

        if (includeSales)
        {
            parameters.Add(new NpgsqlParameter("sale_completed", NpgsqlDbType.Text)
            {
                Value = PosSaleStatus.Completed.ToString(),
            });
            branches.Add(SalesBranch + saleFilters);
        }

        return (string.Join("\nUNION ALL\n", branches), [.. parameters]);
    }

    // Las columnas de las dos ramas, en el mismo orden y con el mismo tipo: UNION ALL las empareja por
    // posición. El costo de la línea se redondea a centavos antes de sumar, igual en las dos.
    private const string OrdersBranch = """
        SELECT i.product_id AS product_id, i.variant_id AS variant_id, o.currency::text AS currency,
               i.quantity AS quantity, i.subtotal AS net_sales, round(i.unit_cost * i.quantity, 2) AS cost,
               (i.unit_cost IS NOT NULL) AS costed, i.product_name::text AS line_name,
               i.product_code::text AS line_code, o.paid_at AS sold_at
        FROM quotations.order_items AS i
        JOIN quotations.orders AS o ON o.id = i.order_id
        WHERE o.tenant_id = @tenant_id AND o.payment_status = @order_paid AND o.status <> @order_cancelled
        """;

    private const string SalesBranch = """
        SELECT sl.product_id AS product_id, sl.variant_id AS variant_id, s.currency::text AS currency,
               sl.quantity AS quantity, sl.subtotal AS net_sales, round(sl.unit_cost * sl.quantity, 2) AS cost,
               (sl.unit_cost IS NOT NULL) AS costed, sl.product_name::text AS line_name,
               sl.product_code::text AS line_code, s.created_at AS sold_at
        FROM pos.sale_lines AS sl
        JOIN pos.sales AS s ON s.id = sl.sale_id
        WHERE s.tenant_id = @tenant_id AND s.status = @sale_completed
        """;

    // Mismo criterio que OrdersReportSource.MapSource: sin default, un valor nuevo rompe acá.
    private static OrderSource? MapSource(OrderSourceFilter value) => value switch
    {
        OrderSourceFilter.Quotation => OrderSource.Quotation,
        OrderSourceFilter.Manual => OrderSource.Manual,
        OrderSourceFilter.Shopify => OrderSource.Shopify,
        OrderSourceFilter.Pos => null,
        OrderSourceFilter.Store => OrderSource.Store,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown order source.")
    };

    // Clases con init y no records: EF no ve a través del constructor de un record en SqlQuery.
    private sealed class CurrencyRow
    {
        public string Currency { get; init; } = string.Empty;

        public decimal NetSales { get; init; }

        public decimal Cost { get; init; }

        public int UncostedLines { get; init; }

        public decimal UncostedNetSales { get; init; }
    }

    private sealed class ProductRow
    {
        public Guid ProductId { get; init; }

        public string Currency { get; init; } = string.Empty;

        public string ProductName { get; init; } = string.Empty;

        public decimal Quantity { get; init; }

        public decimal NetSales { get; init; }

        public decimal Cost { get; init; }
    }

    private sealed class VariantRow
    {
        public Guid ProductId { get; init; }

        public string Currency { get; init; } = string.Empty;

        public Guid VariantId { get; init; }

        public string VariantName { get; init; } = string.Empty;

        public decimal Quantity { get; init; }

        public decimal NetSales { get; init; }

        public decimal Cost { get; init; }
    }
}
