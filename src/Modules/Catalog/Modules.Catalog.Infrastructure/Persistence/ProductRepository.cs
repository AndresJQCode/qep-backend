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
    public Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken) =>
        dbContext.Products.SingleOrDefaultAsync(
            product => product.TenantId == tenantId && product.Id == productId,
            cancellationToken);

    public void Add(Product product) => dbContext.Products.Add(product);
}
