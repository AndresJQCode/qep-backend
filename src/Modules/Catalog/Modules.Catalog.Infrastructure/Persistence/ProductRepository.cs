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

    public async Task<IReadOnlyList<Product>> SearchAsync(
        Guid tenantId,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Products
            .AsNoTracking()
            // CAT-09: sin este Include, PriceScales llega vacío en cada listado — EF no
            // trae colecciones hijas por su cuenta.
            .Include(product => product.PriceScales)
            .Where(product => product.TenantId == tenantId);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
            // sólo los dos que pone esta línea: `%` y `_` son comodines de LIKE, así que un
            // término sin escapar los convierte en parte de la sintaxis. `?search=_` devolvía el
            // catálogo entero —coincide con cualquier carácter—, que es lo contrario de filtrar.
            // Lo encontró la revisión de fiabilidad de CAT-02, contra un comentario previo que
            // afirmaba justo lo que no pasaba.
            var pattern = $"%{EscapeLikeWildcards(term)}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(product.Code, pattern, LikeEscapeCharacter));
        }

        return await query
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken);
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

    public void Add(Product product) => dbContext.Products.Add(product);

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
