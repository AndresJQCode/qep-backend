using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El origen del reporte de cambios de precio: <c>catalog.product_price_changes</c>, con el
/// nombre del producto resuelto por join y el codigo y el nombre visible de la variante leidos
/// aparte, en lote. Ver <see cref="OrdersReportSource"/> sobre por que los adaptadores de
/// <c>reporting</c> viven en el composition root.
///
/// **El cambio es de una variante** (spec 2026-10-20 §3.5). El producto ya no tiene codigo (vive
/// en <c>product_variants</c>), asi que el codigo y el nombre salen de la variante; el nombre
/// visible se arma con <see cref="CatalogVariantReads"/>, el mismo camino de las lineas y el POS,
/// para que un reporte no nombre distinto a la variante. No se cargan los agregados <c>Product</c>:
/// con galeria y hasta 2.048 variantes cada uno, una pagina de 50 filas seria desproporcionada.
///
/// **Es el historico del catalogo, no el de las cotizaciones.** Sigue el precio base en cada moneda del
/// producto y el descuento de una escala; los precios de una linea de cotizacion no pasan por
/// aca.
///
/// Devuelve <see cref="PriceChangeReportRow"/> y no el DTO, a diferencia de los otros tres
/// adaptadores: la diferencia es una regla —un lado nulo cuenta como cero— y se calcula en
/// Application, donde una prueba unitaria la alcanza.
/// </summary>
internal sealed class PriceChangeReportSource(
    CatalogDbContext catalog,
    IOptionDefinitionRepository options,
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

        return (await ResolveRowsAsync(criteria.TenantId, rows, cancellationToken), total);
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

        var monthly = await SummarizeByMonthAsync(changes, criteria.Period.TimeZone, cancellationToken);
        var byField = await SummarizeByFieldAsync(changes, cancellationToken);
        var byProduct = await RankProductsAsync(
            criteria.TenantId, changes, rankSize, totals.Count, productCount, cancellationToken);

        return new PriceChangeReportAggregate(
            totals.Count, productCount, totals.Increase, totals.Decrease,
            monthly, byField, byProduct);
    }

    /// <summary>
    /// La serie mensual por fecha del cambio en el mes del tenant (spec 2026-09-17, punto 5),
    /// agrupada en SQL con <c>AT TIME ZONE</c>. Ver <see cref="OrdersReportSource"/>.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var timeZoneId = timeZone.Id;
        var months = await changes
            .GroupBy(change => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(change.ChangedAt.UtcDateTime, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(change.ChangedAt.UtcDateTime, timeZoneId).Month,
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

    /// <summary>One PriceBase slice per catalogue currency plus ScaleDiscount, always, even at
    /// zero: see <c>PriceChangeFieldSliceDto</c>.</summary>
    private static async Task<IReadOnlyList<PriceChangeFieldSliceDto>> SummarizeByFieldAsync(
        IQueryable<ProductPriceChange> changes,
        CancellationToken cancellationToken)
    {
        var slices = await changes
            .GroupBy(change => new { change.Field, change.Currency })
            .Select(group => new { group.Key.Field, group.Key.Currency, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Trim: a character(3) column pads shorter values; catalogue codes are always three
        // letters, so this only makes the lookup independent of the column type.
        var found = slices.ToDictionary(slice => (slice.Field, slice.Currency?.Trim()), slice => slice.Count);
        return FieldSlices(key => found.GetValueOrDefault(key));
    }

    /// <summary>
    /// Los productos mas retocados, con el resto plegado en una fila "Otros" y, dentro de cada
    /// producto, el desglose por variante.
    ///
    /// Desempate por id: sin un orden total, dos productos con la misma cantidad de cambios pueden
    /// intercambiarse entre dos llamadas identicas y el ranking "parpadea".
    ///
    /// El plegado no reusa <c>ReportRankFolding</c> porque esa fila lleva un <c>Total</c> de
    /// dinero que este reporte no puede sumar; la regla que si se copia es la que importa: el
    /// resto sale **por resta contra el total ya calculado**, no con una consulta mas.
    ///
    /// El desglose son dos consultas para el ranking entero, no una por producto: los cambios por
    /// variante de los productos del ranking, y cuantas variantes tiene cada uno (para saber si su
    /// codigo se puede decir, ver <see cref="PriceChangeProductEntryDto"/>).
    /// </summary>
    private async Task<IReadOnlyList<PriceChangeProductEntryDto>> RankProductsAsync(
        Guid tenantId,
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
            .Select(product => new { product.Id, product.Name })
            .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);

        // Sobre `changes`, que ya trae todos los filtros: los Count de las variantes suman el del
        // producto porque salen del mismo conjunto.
        var perVariant = await changes
            .Where(change => ids.Contains(change.ProductId))
            .GroupBy(change => change.VariantId)
            .Select(group => new { VariantId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var labels = await LoadLabelsAsync(
            tenantId,
            perVariant.Select(entry => entry.VariantId.Value).ToArray(),
            products.ToDictionary(product => product.Key.Value, product => product.Value),
            cancellationToken);

        var variantTotals = await catalog.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && ids.Contains(variant.ProductId))
            .GroupBy(variant => variant.ProductId)
            .Select(group => new { ProductId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.ProductId, entry => entry.Count, cancellationToken);

        var breakdown = perVariant
            .Where(entry => labels.ContainsKey(entry.VariantId.Value))
            .Select(entry => (Label: labels[entry.VariantId.Value], entry.Count))
            .GroupBy(entry => entry.Label.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PriceChangeVariantEntryDto>)group
                    .OrderBy(entry => entry.Label.Position)
                    .ThenBy(entry => entry.Label.VariantId)
                    .Select(entry => new PriceChangeVariantEntryDto(
                        entry.Label.VariantId, entry.Label.Name, entry.Label.Code, entry.Count))
                    .ToArray());

        var ranked = top
            .Select(entry =>
            {
                var variants = breakdown.GetValueOrDefault(entry.ProductId.Value) ?? [];
                // El codigo del producto es el de su unica variante; con varias no hay uno que
                // decir. El total sale de product_variants y no del desglose, porque el desglose
                // solo trae las variantes que cambiaron.
                var code = variantTotals.GetValueOrDefault(entry.ProductId) == 1 && variants.Count == 1
                    ? variants[0].Code
                    : null;
                return new PriceChangeProductEntryDto(
                    entry.ProductId.Value,
                    products.GetValueOrDefault(entry.ProductId),
                    code,
                    EntityCount: 1,
                    entry.Count,
                    variants);
            })
            .ToList();

        var remaining = productCount - ranked.Count;
        if (ranked.Count == rankSize && remaining > 0)
        {
            ranked.Add(new PriceChangeProductEntryDto(
                ProductId: null,
                ProductName: null,
                ProductCode: null,
                remaining,
                totalCount - ranked.Sum(entry => entry.Count),
                Variants: []));
        }

        return ranked;
    }

    /// <summary>
    /// El codigo y el nombre visible de esas variantes, en lote: una consulta de variantes, una de
    /// la biblioteca de opciones (solo si alguna variante tiene valores) y los nombres de producto
    /// que quien llama ya leyo. Un valor cuya opcion no se encontro se omite del nombre, igual que
    /// en el detalle del producto: ver <c>CatalogVariantReads.DisplayNameOf</c>.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, VariantLabel>> LoadLabelsAsync(
        Guid tenantId,
        Guid[] variantIds,
        IReadOnlyDictionary<Guid, string> productNames,
        CancellationToken cancellationToken)
    {
        if (variantIds.Length == 0)
        {
            return new Dictionary<Guid, VariantLabel>();
        }

        var ids = variantIds.Select(id => new VariantId(id)).ToArray();
        var variants = await catalog.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && ids.Contains(variant.Id))
            .ToListAsync(cancellationToken);

        var valueNames = variants.Any(variant => variant.OptionValueIds.Count > 0)
            ? await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, cancellationToken)
            : new Dictionary<Guid, string>();

        return variants.ToDictionary(
            variant => variant.Id.Value,
            variant => new VariantLabel(
                variant.Id.Value,
                variant.ProductId.Value,
                variant.Code,
                CatalogVariantReads.DisplayNameOf(
                    productNames.GetValueOrDefault(variant.ProductId.Value, string.Empty),
                    variant,
                    valueNames),
                variant.Position));
    }

    private static PriceChangeFieldSliceDto[] EmptyFieldSlices() => FieldSlices(_ => 0);

    private static PriceChangeFieldSliceDto[] FieldSlices(Func<(ProductPriceField, string?), int> countOf) =>
    [
        .. Currencies.All.Select(currency => new PriceChangeFieldSliceDto(
            nameof(PriceChangeField.PriceBase), currency.Code, countOf((ProductPriceField.PriceBase, currency.Code)))),
        new PriceChangeFieldSliceDto(
            nameof(PriceChangeField.ScaleDiscount), null, countOf((ProductPriceField.ScaleDiscount, null))),
    ];

    private IQueryable<ChangeRow> BuildQuery(PriceChangeReportCriteria criteria)
    {
        var changes = FilterChanges(criteria);

        // Join interno y no izquierdo: la FK product_id -> catalog.products es real y en cascada,
        // asi que una fila del historico sin su producto no existe.
        var joined = from change in changes
                     join product in catalog.Products.AsNoTracking()
                         on change.ProductId equals product.Id
                     select new { change, product };

        // Ver OrdersReportSource sobre el orden total.
        return joined
            .OrderByDescending(row => row.change.ChangedAt)
            .ThenBy(row => row.change.Id)
            .Select(row => new ChangeRow(
                row.change.Id,
                row.change.ProductId,
                row.change.VariantId,
                row.product.Name,
                row.change.Field,
                row.change.Currency,
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

        if (criteria.Period.Start is { } start)
        {
            changes = changes.Where(change => change.ChangedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            changes = changes.Where(change => change.ChangedAt < end);
        }

        if (criteria.ProductId is { } productId)
        {
            var product = new ProductId(productId);
            changes = changes.Where(change => change.ProductId == product);
        }

        if (criteria.VariantId is { } variantId)
        {
            var variant = new VariantId(variantId);
            changes = changes.Where(change => change.VariantId == variant);
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

        if (criteria.Currency is { } currency)
        {
            changes = changes.Where(change => change.Currency == currency);
        }

        return changes;
    }

    // Una consulta de emails y una de variantes para toda la pagina, no una por fila.
    private async Task<IReadOnlyList<PriceChangeReportRow>> ResolveRowsAsync(
        Guid tenantId,
        IReadOnlyList<ChangeRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // ChangedBy es el subject de la ejecucion, o sea el id de identity.users directo: a
        // diferencia de AdvisorId en pedidos y cotizaciones, no pasa por una membresia.
        var authors = await peopleLookup.EmailsByUserIdAsync(
            rows.Select(row => row.ChangedBy).ToArray(), cancellationToken);

        var labels = await LoadLabelsAsync(
            tenantId,
            rows.Select(row => row.VariantId.Value).Distinct().ToArray(),
            rows.DistinctBy(row => row.ProductId).ToDictionary(row => row.ProductId.Value, row => row.ProductName),
            cancellationToken);

        return rows
            .Select(row =>
            {
                // La FK variant_id -> product_variants es real y en cascada: una fila sin su
                // variante solo existe si se borro entre las dos lecturas, y ese borrado se llevo
                // tambien la fila. Se degrada al nombre del producto antes que tumbar la pagina.
                labels.TryGetValue(row.VariantId.Value, out var label);
                return new PriceChangeReportRow(
                    row.ChangeId.Value,
                    row.ProductId.Value,
                    row.VariantId.Value,
                    label?.Code ?? string.Empty,
                    row.ProductName,
                    label?.Name ?? row.ProductName,
                    MapField(row.Field),
                    row.Currency?.Trim(),
                    row.ScaleFromUnit,
                    row.ScaleToUnit,
                    row.PreviousValue,
                    row.NewValue,
                    row.ChangedBy,
                    authors.GetValueOrDefault(row.ChangedBy),
                    row.ChangedAt);
            })
            .ToArray();
    }

    // Sin default a proposito, en las dos direcciones: ver MapPaymentStatus en OrdersReportSource.
    private static ProductPriceField MapField(PriceChangeField value) => value switch
    {
        PriceChangeField.PriceBase => ProductPriceField.PriceBase,
        PriceChangeField.ScaleDiscount => ProductPriceField.ScaleDiscount,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown price field.")
    };

    private static PriceChangeField MapField(ProductPriceField value) => value switch
    {
        ProductPriceField.PriceBase => PriceChangeField.PriceBase,
        ProductPriceField.ScaleDiscount => PriceChangeField.ScaleDiscount,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown price field.")
    };

    private sealed record ChangeRow(
        ProductPriceChangeId ChangeId,
        ProductId ProductId,
        VariantId VariantId,
        string ProductName,
        ProductPriceField Field,
        string? Currency,
        int? ScaleFromUnit,
        int? ScaleToUnit,
        decimal? PreviousValue,
        decimal? NewValue,
        Guid ChangedBy,
        DateTimeOffset ChangedAt);

    private sealed record VariantLabel(Guid VariantId, Guid ProductId, string Code, string Name, int Position);
}
