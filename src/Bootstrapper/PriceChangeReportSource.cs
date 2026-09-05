using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Bootstrapper;

/// <summary>
/// El origen del reporte de cambios de precio: <c>catalog.product_price_changes</c>, con el
/// nombre y el codigo del producto resueltos por join. Ver <see cref="SalesReportSource"/> sobre
/// por que los adaptadores de <c>reporting</c> viven en el composition root.
///
/// **Es el historico del catalogo, no el de las cotizaciones.** Sigue los dos precios base del
/// producto y el descuento de una escala; los precios de una linea de cotizacion no pasan por
/// aca.
///
/// Devuelve <see cref="PriceChangeReportRow"/> y no el DTO, a diferencia de los otros tres
/// adaptadores: la diferencia es una regla —un lado nulo cuenta como cero— y se calcula en
/// Application, donde una prueba unitaria la alcanza.
/// </summary>
internal sealed class PriceChangeReportSource(
    CatalogDbContext catalog,
    ReportingPeopleLookup peopleLookup) : IPriceChangeReportSource
{
    public async Task<(IReadOnlyList<PriceChangeReportRow> Rows, int Total)> ListAsync(
        PriceChangeReportCriteria criteria,
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

        return (await ResolveAuthorsAsync(rows, cancellationToken), total);
    }

    public async Task<IReadOnlyList<PriceChangeReportRow>> ListForExportAsync(
        PriceChangeReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await BuildQuery(criteria).Take(limit).ToListAsync(cancellationToken);
        return await ResolveAuthorsAsync(rows, cancellationToken);
    }

    public async Task<PriceChangeReportAggregate> SummarizeAsync(
        PriceChangeReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var changes = FilterChanges(criteria);

        // GroupBy sobre una constante es el "agregar todo el conjunto", que traduce a un SELECT
        // con agregados y sin GROUP BY. Sobre cero filas no devuelve ninguna, y de ahi el
        // fallback: un resumen vacio es cero, nunca nulo.
        //
        // La direccion se cuenta con la misma regla que PriceChangeDifference —un lado nulo vale
        // cero—, escrita como expresion porque un metodo de dominio no lo traduce EF. Contarla en
        // memoria exigiria traerse el periodo entero, que es lo que este resumen evita.
        var totals = await changes
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Increase = group.Count(change =>
                    (change.NewValue ?? 0m) - (change.PreviousValue ?? 0m) > 0m),
                Decrease = group.Count(change =>
                    (change.NewValue ?? 0m) - (change.PreviousValue ?? 0m) < 0m),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (totals is null || totals.Count == 0)
        {
            return new PriceChangeReportAggregate(0, 0, 0, 0, [], EmptyFieldSlices(), []);
        }

        // Los productos distintos tocados: el denominador que le falta al conteo de cambios para
        // saber si fueron muchos sobre pocos productos o al reves. Sirve ademas como el "cuantas
        // entidades hay" del plegado de "Otros", asi que se pide una sola vez.
        var productCount = await changes
            .Select(change => change.ProductId)
            .Distinct()
            .CountAsync(cancellationToken);

        var monthly = await SummarizeByMonthAsync(changes, cancellationToken);
        var byField = await SummarizeByFieldAsync(changes, cancellationToken);
        var byProduct = await RankProductsAsync(
            changes, rankSize, totals.Count, productCount, cancellationToken);

        return new PriceChangeReportAggregate(
            totals.Count, productCount, totals.Increase, totals.Decrease,
            monthly, byField, byProduct);
    }

    /// <summary>
    /// La serie mensual por fecha del cambio, en **UTC** — el mismo huso en el que
    /// <c>ReportDateRange</c> corta el rango. Agrupar en el huso de la sesion de PostgreSQL
    /// pondria un cambio del 1 de enero en diciembre para un tenant en America/Bogota.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        CancellationToken cancellationToken)
    {
        var months = await changes
            .GroupBy(change => new
            {
                change.ChangedAt.UtcDateTime.Year,
                change.ChangedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportCountPointDto(point.Year, point.Month, point.Count))
            .ToArray();
    }

    /// <summary>Los tres campos, siempre, incluso en cero: ver
    /// <c>PriceChangeFieldSliceDto</c>.</summary>
    private static async Task<IReadOnlyList<PriceChangeFieldSliceDto>> SummarizeByFieldAsync(
        IQueryable<ProductPriceChange> changes,
        CancellationToken cancellationToken)
    {
        var slices = await changes
            .GroupBy(change => change.Field)
            .Select(group => new { Field = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var found = slices.ToDictionary(slice => slice.Field, slice => slice.Count);

        return Enum.GetValues<ProductPriceField>()
            .Select(field => new PriceChangeFieldSliceDto(
                MapField(field).ToString(),
                found.GetValueOrDefault(field)))
            .ToArray();
    }

    /// <summary>
    /// Los productos mas retocados, con el resto plegado en una fila "Otros".
    ///
    /// Desempate por id: sin un orden total, dos productos con la misma cantidad de cambios pueden
    /// intercambiarse entre dos llamadas identicas y el ranking "parpadea".
    ///
    /// El plegado no reusa <c>ReportRankFolding</c> porque esa fila lleva un <c>Total</c> de
    /// dinero que este reporte no puede sumar; la regla que si se copia es la que importa: el
    /// resto sale **por resta contra el total ya calculado**, no con una consulta mas.
    /// </summary>
    private async Task<IReadOnlyList<PriceChangeProductEntryDto>> RankProductsAsync(
        IQueryable<ProductPriceChange> changes,
        int rankSize,
        int totalCount,
        int productCount,
        CancellationToken cancellationToken)
    {
        if (rankSize <= 0)
        {
            return [];
        }

        var top = await changes
            .GroupBy(change => change.ProductId)
            .Select(group => new { ProductId = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.ProductId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var ids = top.Select(entry => entry.ProductId).ToArray();
        // Una consulta de nombres para el ranking entero, no una por fila.
        var products = await catalog.Products
            .AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, product.Code })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var ranked = top
            .Select(entry => new PriceChangeProductEntryDto(
                entry.ProductId.Value,
                products.GetValueOrDefault(entry.ProductId)?.Name,
                products.GetValueOrDefault(entry.ProductId)?.Code,
                EntityCount: 1,
                entry.Count))
            .ToList();

        var remaining = productCount - ranked.Count;
        if (ranked.Count == rankSize && remaining > 0)
        {
            ranked.Add(new PriceChangeProductEntryDto(
                ProductId: null,
                ProductName: null,
                ProductCode: null,
                remaining,
                totalCount - ranked.Sum(entry => entry.Count)));
        }

        return ranked;
    }

    private static PriceChangeFieldSliceDto[] EmptyFieldSlices() =>
        Enum.GetValues<ProductPriceField>()
            .Select(field => new PriceChangeFieldSliceDto(MapField(field).ToString(), 0))
            .ToArray();

    private IQueryable<ChangeRow> BuildQuery(PriceChangeReportCriteria criteria)
    {
        var changes = FilterChanges(criteria);

        // Join interno y no izquierdo: la FK product_id -> catalog.products es real y en cascada,
        // asi que una fila del historico sin su producto no existe.
        var joined = from change in changes
                     join product in catalog.Products.AsNoTracking()
                         on change.ProductId equals product.Id
                     select new { change, product };

        // Ver SalesReportSource sobre el orden total.
        return joined
            .OrderByDescending(row => row.change.ChangedAt)
            .ThenBy(row => row.change.Id)
            .Select(row => new ChangeRow(
                row.change.Id,
                row.change.ProductId,
                row.product.Code,
                row.product.Name,
                row.change.Field,
                row.change.ScaleFromUnit,
                row.change.ScaleToUnit,
                row.change.PreviousValue,
                row.change.NewValue,
                row.change.ChangedBy,
                row.change.ChangedAt));
    }

    /// <summary>
    /// Los filtros del reporte, compartidos por el listado y el resumen: que los dos salgan de
    /// aca es lo que hace imposible que el panel y la tabla hablen de conjuntos distintos.
    /// </summary>
    private IQueryable<ProductPriceChange> FilterChanges(PriceChangeReportCriteria criteria)
    {
        var changes = catalog.ProductPriceChanges
            .AsNoTracking()
            .Where(change => change.TenantId == criteria.TenantId);

        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            changes = changes.Where(change => change.ChangedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            changes = changes.Where(change => change.ChangedAt < end);
        }

        if (criteria.ProductId is { } productId)
        {
            var product = new ProductId(productId);
            changes = changes.Where(change => change.ProductId == product);
        }

        if (criteria.ChangedBy is { } changedBy)
        {
            changes = changes.Where(change => change.ChangedBy == changedBy);
        }

        if (criteria.Field is { } field)
        {
            var mapped = MapField(field);
            changes = changes.Where(change => change.Field == mapped);
        }

        return changes;
    }

    // Una consulta de emails para toda la pagina, no una por fila.
    private async Task<IReadOnlyList<PriceChangeReportRow>> ResolveAuthorsAsync(
        IReadOnlyList<ChangeRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // ChangedBy es el subject de la ejecucion, o sea el id de identity.users directo: a
        // diferencia de AdvisorId en ventas y cotizaciones, no pasa por una membresia.
        var authors = await peopleLookup.EmailsByUserIdAsync(
            rows.Select(row => row.ChangedBy).ToArray(), cancellationToken);

        return rows
            .Select(row => new PriceChangeReportRow(
                row.ChangeId.Value,
                row.ProductId.Value,
                row.ProductCode,
                row.ProductName,
                MapField(row.Field),
                row.ScaleFromUnit,
                row.ScaleToUnit,
                row.PreviousValue,
                row.NewValue,
                row.ChangedBy,
                authors.GetValueOrDefault(row.ChangedBy),
                row.ChangedAt))
            .ToArray();
    }

    // Sin default a proposito, en las dos direcciones: ver MapPaymentStatus en SalesReportSource.
    private static ProductPriceField MapField(PriceChangeField value) => value switch
    {
        PriceChangeField.PriceBaseUsd => ProductPriceField.PriceBaseUsd,
        PriceChangeField.PriceBaseCop => ProductPriceField.PriceBaseCop,
        PriceChangeField.ScaleDiscount => ProductPriceField.ScaleDiscount,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown price field.")
    };

    private static PriceChangeField MapField(ProductPriceField value) => value switch
    {
        ProductPriceField.PriceBaseUsd => PriceChangeField.PriceBaseUsd,
        ProductPriceField.PriceBaseCop => PriceChangeField.PriceBaseCop,
        ProductPriceField.ScaleDiscount => PriceChangeField.ScaleDiscount,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown price field.")
    };

    private sealed record ChangeRow(
        ProductPriceChangeId ChangeId,
        ProductId ProductId,
        string ProductCode,
        string ProductName,
        ProductPriceField Field,
        int? ScaleFromUnit,
        int? ScaleToUnit,
        decimal? PreviousValue,
        decimal? NewValue,
        Guid ChangedBy,
        DateTimeOffset ChangedAt);
}
