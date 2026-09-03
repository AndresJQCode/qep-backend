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
        var query = dbContext.Products
            .AsNoTracking()
            // CAT-09: sin este Include, PriceScales llega vacío en cada listado — EF no
            // trae colecciones hijas por su cuenta.
            .Include(product => product.PriceScales)
            .Where(product => product.TenantId == tenantId);

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
                EF.Functions.ILike(product.Code, searchPattern, LikeEscapeCharacter));
        }

        // Las dos cajas separadas del listado (CAT-FILTROS-01), cada una filtra su propia
        // columna y se combinan con AND cuando el llamador manda las dos.
        var namePattern = LikePattern(name);
        if (namePattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, namePattern, LikeEscapeCharacter));
        }

        var codePattern = LikePattern(code);
        if (codePattern is not null)
        {
            query = query.Where(product =>
                EF.Functions.ILike(product.Code, codePattern, LikeEscapeCharacter));
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

        var orderedQuery = searchPattern is not null
            ? query.OrderByDescending(product =>
                EF.Functions.TrigramsSimilarity(product.Name, searchTerm) +
                EF.Functions.TrigramsSimilarity(product.Code, searchTerm))
            : namePattern is not null || codePattern is not null
                ? query.OrderByDescending(product =>
                    EF.Functions.TrigramsSimilarity(product.Name, nameTerm) +
                    EF.Functions.TrigramsSimilarity(product.Code, codeTerm))
                : query.OrderBy(product => product.Name);

        var items = await orderedQuery
            .ThenBy(product => product.Name)
            .ThenBy(product => product.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Con tracking a propósito, a diferencia de SearchAsync: los llamadores de éste mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    //
    // El Include acá no es sólo para que la lectura no vuelva vacía: sin las escalas viejas
    // en el change tracker, ApplyPricing().Clear() no las ve, y un Update no las reemplaza —
    // las deja huérfanas en la base y sólo inserta las nuevas encima.
    public Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken) =>
        dbContext.Products
            .Include(product => product.PriceScales)
            .SingleOrDefaultAsync(
                product => product.TenantId == tenantId && product.Id == productId,
                cancellationToken);

    public async Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new HashSet<string>();
        }

        var existing = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId && codes.Contains(product.Code))
            .Select(product => product.Code)
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
