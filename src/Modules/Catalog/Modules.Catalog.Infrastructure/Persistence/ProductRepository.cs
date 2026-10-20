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
        // Mismo Include que SearchAsync y por el mismo motivo: sin el, las escalas llegan
        // vacias y el export perderia justamente sus columnas.
        var query = Aggregates()
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
        // Con variantes, el código menor del producto: la fila por variante del Excel es de la
        // Tarea 8, que decide su orden dentro del producto.
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
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // CAT-09: sin los Include, las escalas llegan vacías en cada listado — EF no trae
        // colecciones hijas por su cuenta.
        var (ordered, total) = await SearchQueryAsync(
            Aggregates().AsNoTracking(), tenantId, search, name, code, isActive, cancellationToken);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Variantes, Tarea 5: el listado de productos no trae el agregado (con 2.048 variantes por
    // producto y sus escalas, una página de 200 serían cientos de miles de filas para pintar una
    // grilla). Proyecta lo que la fila muestra y, en una segunda consulta agrupada en la base, el
    // rango de precios de las variantes activas de la página.
    public async Task<(IReadOnlyList<ProductSummary> Items, int Total)> SearchSummariesAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var (ordered, total) = await SearchQueryAsync(
            dbContext.Products.AsNoTracking(), tenantId, search, name, code, isActive, cancellationToken);

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
                ImageFileId = product.Gallery
                    .OrderBy(image => image.Position)
                    .Select(image => (Guid?)image.FileId)
                    .FirstOrDefault(),
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

        // Cuántas variantes tiene cada producto (activas e inactivas) y, para el que tiene una sola,
        // su código: con una, el mínimo es ése.
        var variantStats = await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && productIds.Contains(variant.ProductId))
            .GroupBy(variant => variant.ProductId)
            .Select(group => new
            {
                ProductId = group.Key,
                Count = group.Count(),
                Code = group.Min(variant => variant.Code)
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
                row.ImageFileId,
                variantStats.GetValueOrDefault(row.Id)?.Count ?? 0,
                variantStats.GetValueOrDefault(row.Id) is { Count: 1 } single ? single.Code : null,
                rangesByProduct.GetValueOrDefault(row.Id) ?? [],
                row.Version,
                row.CreatedAt,
                row.UpdatedAt))
            .ToArray();

        return (items, total);
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
        CancellationToken cancellationToken)
    {
        var query = source.Where(product => product.TenantId == tenantId);
        if (isActive is not null)
        {
            query = query.Where(product => product.IsActive == isActive);
        }

        // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
        // sólo los dos que pone LikePattern: `%` y `_` son comodines de LIKE, así que un
        // término sin escapar los convierte en parte de la sintaxis. `?search=_` devolvía el
        // catálogo entero —coincide con cualquier carácter—, que es lo contrario de filtrar.
        // Lo encontró la revisión de fiabilidad de CAT-02, contra un comentario previo que
        // afirmaba justo lo que no pasaba.
        var searchPattern = LikePattern(search);
        if (searchPattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, searchPattern, LikeEscapeCharacter) ||
                product.Variants.Any(variant =>
                    EF.Functions.ILike(variant.Code, searchPattern, LikeEscapeCharacter)));
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
    public Task<Product?> FindByCodeAsync(
        Guid tenantId,
        string code,
        CancellationToken cancellationToken) =>
        FindVariantByCodeAsync(tenantId, code, cancellationToken);

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

    // AsNoTracking y con las escalas: quien la llama arma una respuesta de lectura (el detalle
    // de una cotizacion), no muta el producto.
    public async Task<IReadOnlyList<Product>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        return await Aggregates()
            .AsNoTracking()
            .Where(product =>
                product.TenantId == tenantId && productIds.Contains(product.Id))
            .ToListAsync(cancellationToken);
    }

    // Sin AsNoTracking, al revés que ListByIdsAsync: la copia de escalas muta lo que trae.
    public async Task<IReadOnlyList<Product>> ListByIdsForUpdateAsync(
        Guid tenantId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        return await Aggregates()
            .Where(product =>
                product.TenantId == tenantId && productIds.Contains(product.Id))
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
}
