using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class ProductRepository(CatalogDbContext dbContext) : IProductRepository
{
    private const string LikeEscapeCharacter = "\\";

    // La barra va primero: escaparla después convertiría en literal la barra que acaban de
    // agregar los otros dos reemplazos.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // `null` para un filtro vacío/ausente, para que el llamador sepa si tiene que agregar el
    // `Where` o no — un patrón `"%%"` matchearía todo, que no es lo mismo que "no filtrar".
    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    // El agregado entero: variantes con sus escalas (los precios, las opciones y los valores son
    // owned y vienen solos) y la galería. Sin las escalas en el change tracker, ReplaceScales no
    // las ve y un Update las deja huérfanas; sin las variantes, el producto no tiene código ni
    // precio. Split: con hasta 2.048 variantes, un JOIN único multiplica filas por escalas.
    private IQueryable<Product> Aggregates() => dbContext.Products
        .Include(product => product.Variants)
            .ThenInclude(variant => variant.PriceScales)
        .Include(product => product.Gallery)
        .AsSplitQuery();

    public async Task<IReadOnlyList<Product>> ListForExportAsync(
        Guid tenantId,
        string? name,
        string? code,
        bool? isActive,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Variantes con sus escalas y nada más (los precios, los valores de opción y los empaques son
        // owned y vienen solos): el Excel no pinta la galería, y con hasta 2.048 variantes por
        // producto el lote de 500 productos ya es pesado sin traer además sus fotos. No se proyecta
        // a un tipo de lectura a propósito: las escalas y los precios son colecciones anidadas y la
        // columna de cada moneda y de cada escala sale del conjunto completo, así que reescribir
        // el grafo como proyección cambiaría la consulta más de lo que ahorra.
        var query = dbContext.Products
            .Include(product => product.Variants)
                .ThenInclude(variant => variant.PriceScales)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId);

        if (isActive is not null)
        {
            query = query.Where(product => product.IsActive == isActive);
        }

        var namePattern = LikePattern(name);
        if (namePattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, namePattern, LikeEscapeCharacter));
        }

        var codePattern = LikePattern(code);
        if (codePattern is not null)
        {
            query = query.Where(product => product.Variants.Any(variant =>
                EF.Functions.ILike(variant.Code, codePattern, LikeEscapeCharacter)));
        }

        // Por codigo y no por relevancia: una planilla se lee y se compara en un orden estable,
        // y el export no tiene un termino de busqueda contra el cual medir relevancia.
        // Orden estable antes de paginar: sin el, dos lotes consecutivos pueden repetir o
        // saltear filas. Por codigo y no por relevancia — el export no tiene termino contra el
        // cual medirla, y una planilla se compara en un orden previsible.
        // Con variantes, el código menor del producto: el Excel escribe una fila por variante y
        // las agrupa por producto, así que el orden entre productos es éste y dentro de cada uno
        // lo da `Position`.
        return await query
            .OrderBy(product => product.Variants.Min(variant => variant.Code))
            .ThenBy(product => product.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // CAT-09: sin los Include, las escalas llegan vacías en cada listado — EF no trae
        // colecciones hijas por su cuenta.
        var (ordered, total) = await SearchQueryAsync(
            Aggregates().AsNoTracking(), tenantId, search, name, code, isActive, categoryIds, cancellationToken);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Con variantes, el listado de productos no trae el agregado (con 2.048 variantes por
    // producto y sus escalas, una página de 200 serían cientos de miles de filas para pintar una
    // grilla). Proyecta lo que la fila muestra y, en una segunda consulta agrupada en la base, el
    // rango de precios de las variantes activas de la página.
    public async Task<(IReadOnlyList<ProductSummary> Items, int Total)> SearchSummariesAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var (ordered, total) = await SearchQueryAsync(
            dbContext.Products.AsNoTracking(), tenantId, search, name, code, isActive, categoryIds, cancellationToken);

        var rows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(product => new
            {
                product.Id,
                product.Name,
                product.IsActive,
                product.Description,
                product.TaxRateId,
                product.CategoryId,
                ImageFileId = product.Gallery
                    .OrderBy(image => image.Position)
                    .Select(image => (Guid?)image.FileId)
                    .FirstOrDefault(),
                HasOptions = EF.Property<IEnumerable<ProductOption>>(product, "_options").Any(),
                product.Version,
                product.CreatedAt,
                product.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return ([], total);
        }

        var productIds = rows.Select(row => row.Id).ToArray();

        // Cuántas variantes tiene cada producto, en total y activas, y el menor código de cada grupo: con
        // una sola, el menor es el suyo. ProductListCode decide cuál muestra la fila.
        var variantStats = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && productIds.Contains(variant.ProductId))
            .GroupBy(variant => variant.ProductId)
            .Select(group => new
            {
                ProductId = group.Key,
                Count = group.Count(),
                Code = group.Min(variant => variant.Code),
                ActiveCount = group.Count(variant => variant.IsActive),
                ActiveCode = group.Where(variant => variant.IsActive).Min(variant => variant.Code)
            })
            .ToDictionaryAsync(stat => stat.ProductId, cancellationToken);
        var ranges = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && variant.IsActive
                && productIds.Contains(variant.ProductId))
            .SelectMany(variant => variant.Prices, (variant, price) => new
            {
                variant.ProductId,
                price.Currency,
                price.Amount
            })
            .GroupBy(price => new { price.ProductId, price.Currency })
            .Select(group => new
            {
                group.Key.ProductId,
                group.Key.Currency,
                Min = group.Min(price => price.Amount),
                Max = group.Max(price => price.Amount)
            })
            .ToListAsync(cancellationToken);

        var rangesByProduct = ranges
            .GroupBy(range => range.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ProductPriceRange>)group
                    .Select(range => new ProductPriceRange(range.Currency, range.Min, range.Max))
                    .ToArray());

        var items = rows
            .Select(row => new ProductSummary(
                row.Id,
                row.Name,
                row.IsActive,
                row.Description,
                row.TaxRateId,
                row.CategoryId,
                row.ImageFileId,
                variantStats.GetValueOrDefault(row.Id)?.Count ?? 0,
                variantStats.GetValueOrDefault(row.Id) is { } stats
                    ? ProductListCode.For(
                        row.HasOptions,
                        new ProductVariantCodeStats(stats.Count, stats.Code, stats.ActiveCount, stats.ActiveCode))
                    : null,
                rangesByProduct.GetValueOrDefault(row.Id) ?? [],
                row.Version,
                row.CreatedAt,
                row.UpdatedAt))
            .ToArray();

        return (items, total);
    }

    // El selector de cotizaciones y pedidos pide las variantes vendibles de
    // la página de productos. Peor caso: 50 productos x 2.048 variantes (el listado con variantes
    // topa la página en 50, ver ProductPaging) = 102.400 filas de variante, otras tantas por
    // moneda en precios y las escalas de cada una. Por eso no se carga el agregado ni se anida: cada
    // consulta es plana, filtrada por tenant y por productos de la página, y se junta en memoria.
    // Un Include/Select anidado sería una sola consulta, pero multiplicaría filas (variante x
    // precio x escala) en el JOIN.
    public async Task<IReadOnlyList<SellableVariantSummary>> ListActiveVariantsAsync(
        Guid tenantId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        var ids = productIds.ToArray();
        var variants = dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && variant.IsActive
                && ids.Contains(variant.ProductId));

        // Los valores se leen por la colección owned `_optionValues` (por nombre, porque la
        // propiedad pública es calculada y EF no la traduce): ver PriceChangeReportSource.
        var rows = await variants
            .Select(variant => new
            {
                variant.Id,
                variant.ProductId,
                variant.Code,
                variant.Barcode,
                variant.Position,
                variant.GalleryImageId,
                variant.PackagingUnits,
                ValueIds = EF.Property<IEnumerable<ProductVariantOptionValue>>(variant, "_optionValues")
                    .OrderBy(value => value.Position)
                    .Select(value => value.OptionValueId)
                    .ToList()
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var prices = (await variants
            .SelectMany(variant => variant.Prices, (variant, price) => new
            {
                VariantId = variant.Id,
                price.Currency,
                price.Amount
            })
            .ToListAsync(cancellationToken))
            .ToLookup(price => price.VariantId);

        var scales = (await (
                from scale in dbContext.PriceScales.AsNoTracking()
                join variant in variants on scale.VariantId equals variant.Id
                select new
                {
                    scale.VariantId,
                    scale.Id,
                    scale.FromUnit,
                    scale.ToUnit,
                    scale.Discount,
                    scale.Restriction,
                    scale.Multiple,
                    scale.AllowGrouping
                })
            .ToListAsync(cancellationToken))
            .ToLookup(scale => scale.VariantId);

        // La foto propia: sólo se consulta la galería si alguna variante eligió una.
        var galleryImageIds = rows
            .Where(row => row.GalleryImageId.HasValue)
            .Select(row => row.GalleryImageId!.Value)
            .Distinct()
            .ToArray();
        var galleryFiles = galleryImageIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await dbContext.Products
                .AsNoTracking()
                .Where(product => product.TenantId == tenantId && ids.Contains(product.Id))
                .SelectMany(product => product.Gallery)
                .Where(image => galleryImageIds.Contains(image.Id))
                .ToDictionaryAsync(image => image.Id, image => image.FileId, cancellationToken);

        return rows
            .OrderBy(row => row.ProductId.Value)
            .ThenBy(row => row.Position)
            .Select(row => new SellableVariantSummary(
                row.ProductId,
                row.Id,
                row.Code,
                row.Barcode,
                row.Position,
                row.ValueIds,
                prices[row.Id].ToDictionary(price => price.Currency, price => price.Amount),
                scales[row.Id]
                    .Select(scale => new SellableVariantScale(
                        scale.Id.Value,
                        scale.FromUnit,
                        scale.ToUnit,
                        scale.Discount,
                        scale.Restriction,
                        scale.Multiple,
                        scale.AllowGrouping))
                    .ToArray(),
                row.PackagingUnits,
                row.GalleryImageId is { } imageId && galleryFiles.TryGetValue(imageId, out var fileId)
                    ? fileId
                    : null))
            .ToArray();
    }

    // El filtro y el orden del listado, compartidos por el que trae el agregado (POS) y el que
    // proyecta la fila (listado de productos): que los dos ordenen y cuenten igual es lo que hace
    // que una misma búsqueda dé la misma página en las dos pantallas.
    private static async Task<(IOrderedQueryable<Product> Ordered, int Total)> SearchQueryAsync(
        IQueryable<Product> source,
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        CancellationToken cancellationToken)
    {
        var query = source.Where(product => product.TenantId == tenantId);
        if (isActive is not null)
        {
            query = query.Where(product => product.IsActive == isActive);
        }

        // null = sin filtro; una colección vacía no devuelve filas. Lo usa el POS; el listado de
        // productos no lo expone todavía (spec 2026-10-26 §3.3).
        if (categoryIds is not null)
        {
            // Los ids con el tipo exacto de la propiedad, ProductCategoryId? (nullable), y el Contains sobre
            // la propiedad entera. Con ProductCategoryId[] contra product.CategoryId.Value, EF le aplicaba
            // al parámetro el conversor de la columna nullable a elementos no nullable y el GET respondía 500
            // (TargetInvocationException; lo encontró PosCategoryApiTests contra Postgres). Un producto sin
            // categoría no entra: null nunca está en la lista.
            var ids = categoryIds.Select(id => (ProductCategoryId?)new ProductCategoryId(id)).ToArray();
            query = query.Where(product => ids.Contains(product.CategoryId));
        }

        // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
        // sólo los dos que pone LikePattern: `%` y `_` son comodines de LIKE, así que un
        // término sin escapar los convierte en parte de la sintaxis. `?search=_` devolvía el
        // catálogo entero —coincide con cualquier carácter—, que es lo contrario de filtrar.
        // Lo encontró la revisión de fiabilidad de CAT-02, contra un comentario previo que
        // afirmaba justo lo que no pasaba.
        //
        // Con variantes, `search` también encuentra por código de barras de una variante
        // (spec 2026-10-20 §5, la búsqueda del POS). Lo usan el POS y el `search` del listado y del
        // combobox de cotizaciones, que también quedan encontrando por código de barras: es la
        // misma caja de búsqueda y no hay motivo para que un código escaneado no la encuentre.
        // name/code (las cajas separadas del listado) no cambian.
        var searchPattern = LikePattern(search);
        if (searchPattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, searchPattern, LikeEscapeCharacter) ||
                product.Variants.Any(variant =>
                    EF.Functions.ILike(variant.Code, searchPattern, LikeEscapeCharacter) ||
                    (variant.Barcode != null &&
                        EF.Functions.ILike(variant.Barcode, searchPattern, LikeEscapeCharacter))));
        }

        // Las dos cajas separadas del listado (CAT-FILTROS-01), cada una filtra su propia
        // columna y se combinan con AND cuando el llamador manda las dos.
        var namePattern = LikePattern(name);
        if (namePattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, namePattern, LikeEscapeCharacter));
        }

        // Un producto coincide si alguna de sus variantes coincide.
        var codePattern = LikePattern(code);
        if (codePattern is not null)
        {
            query = query.Where(product => product.Variants.Any(variant =>
                EF.Functions.ILike(variant.Code, codePattern, LikeEscapeCharacter)));
        }

        // El total se cuenta sobre la consulta ya filtrada y antes de paginar — mismo criterio
        // que CustomerRepository.SearchAsync.
        var total = await query.CountAsync(cancellationToken);

        // Con algún filtro de texto activo, el resultado se ordena por relevancia (la fila que
        // más se parece al término buscado va primero) en vez de alfabéticamente — mismo
        // criterio que CustomerRepository.SearchAsync. `search` (el combobox de quotes) usa un
        // solo término contra los dos campos; name/code (el listado) usan un término
        // independiente por campo.
        // Los `?.`/`??` tienen que resolverse ANTES del lambda — CS8072, mismo motivo que
        // CustomerRepository.SearchAsync.
        var searchTerm = search?.Trim() ?? string.Empty;
        var nameTerm = name?.Trim() ?? string.Empty;
        var codeTerm = code?.Trim() ?? string.Empty;

        // Con variantes, la relevancia del código es la de la variante que más se parece.
        var orderedQuery = searchPattern is not null
            ? query.OrderByDescending(product =>
                EF.Functions.TrigramsSimilarity(product.Name, searchTerm) +
                product.Variants.Max(variant => EF.Functions.TrigramsSimilarity(variant.Code, searchTerm)))
            : namePattern is not null || codePattern is not null
                ? query.OrderByDescending(product =>
                    EF.Functions.TrigramsSimilarity(product.Name, nameTerm) +
                    product.Variants.Max(variant => EF.Functions.TrigramsSimilarity(variant.Code, codeTerm)))
                : query.OrderBy(product => product.Name);

        return (orderedQuery
            .ThenBy(product => product.Name)
            .ThenBy(product => product.Id), total);
    }

    // Con tracking a propósito, a diferencia de SearchAsync: los llamadores de éste mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken) =>
        Aggregates()
            .SingleOrDefaultAsync(
                product => product.TenantId == tenantId && product.Id == productId,
                cancellationToken);

    // Igualdad exacta y con mayúsculas, servida por IX_product_variants_tenant_code: es la misma
    // comparación del índice único y del agregado (ordinal), así que un código que este método no
    // encuentra es uno que se puede guardar.
    public Task<Product?> FindVariantByCodeAsync(
        Guid tenantId,
        string code,
        CancellationToken cancellationToken) =>
        Aggregates()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                product => product.TenantId == tenantId
                    && product.Variants.Any(variant => variant.Code == code),
                cancellationToken);

    public Task<Product?> FindVariantByBarcodeAsync(
        Guid tenantId,
        string barcode,
        CancellationToken cancellationToken) =>
        Aggregates()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                product => product.TenantId == tenantId
                    && product.Variants.Any(variant => variant.Barcode == barcode),
                cancellationToken);

    public async Task<IReadOnlyList<Product>> FindVariantsByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<VariantId> variantIds,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        return await Aggregates()
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId
                && product.Variants.Any(variant => variantIds.Contains(variant.Id)))
            .ToListAsync(cancellationToken);
    }

    // Sin AsNoTracking, al revés que FindVariantsByIdsAsync: la copia de escalas muta lo que trae.
    public async Task<IReadOnlyList<Product>> FindVariantsByIdsForUpdateAsync(
        Guid tenantId,
        IReadOnlyCollection<VariantId> variantIds,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        return await Aggregates()
            .Where(product => product.TenantId == tenantId
                && product.Variants.Any(variant => variantIds.Contains(variant.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new HashSet<string>();
        }

        // Entre todas las variantes del tenant, que es donde vive la unicidad del código.
        var existing = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && codes.Contains(variant.Code))
            .Select(variant => variant.Code)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }

    public async Task<IReadOnlySet<string>> FindExistingBarcodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> barcodes,
        CancellationToken cancellationToken)
    {
        if (barcodes.Count == 0)
        {
            return new HashSet<string>();
        }

        var existing = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && variant.Barcode != null
                && barcodes.Contains(variant.Barcode))
            .Select(variant => variant.Barcode!)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }

    public void Add(Product product) => dbContext.Products.Add(product);

    // AddRange y no un SaveChanges propio: las filas quedan en el change tracker y salen en el
    // commit de CatalogUnitOfWork, junto al producto que las originó. Una lista vacía —el caso
    // normal de un PUT que no toca precios— es un no-op de EF, así que no hace falta guardarla.
    public void AddPriceChanges(IReadOnlyList<ProductPriceChange> changes) =>
        dbContext.ProductPriceChanges.AddRange(changes);

    // AnyAsync y no un Count: la pregunta es si hay al menos uno, y PostgreSQL puede cortar en
    // el primero. AsNoTracking está de más acá porque Any no materializa entidades.
    public Task<bool> AnyWithTaxRateAsync(
        Guid tenantId,
        TaxRateId taxRateId,
        CancellationToken cancellationToken) =>
        dbContext.Products.AnyAsync(
            product => product.TenantId == tenantId && product.TaxRateId == taxRateId,
            cancellationToken);

    // CountAsync sobre el DbSet: un SELECT count(*) sin materializar productos ni variantes.
    public Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Products.CountAsync(
            product => product.TenantId == tenantId && product.IsActive,
            cancellationToken);
}
