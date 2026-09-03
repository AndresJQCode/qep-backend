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

    private IQueryable<ChangeRow> BuildQuery(PriceChangeReportCriteria criteria)
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
