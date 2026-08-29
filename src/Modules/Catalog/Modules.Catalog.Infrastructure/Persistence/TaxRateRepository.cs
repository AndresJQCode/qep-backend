using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class TaxRateRepository(CatalogDbContext dbContext) : ITaxRateRepository
{
    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // Sin ILike ni escapado de comodines en ListAsync, a diferencia de ProductRepository: este
    // recurso no expone búsqueda por texto libre. Ese es también el motivo por el que no puede
    // repetir el defecto de `?search=_` que encontró la revisión de fiabilidad de CAT-02.
    // FindByNameAsync sí usa ILike — es una coincidencia exacta case-insensitive, no un filtro de
    // listado, pero igual necesita escapar los comodines de LIKE.
    public async Task<IReadOnlyList<TaxRate>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.TaxRates
            .AsNoTracking()
            .Where(taxRate => taxRate.TenantId == tenantId)
            .OrderBy(taxRate => taxRate.Name)
            .ToListAsync(cancellationToken);

    // Con tracking a propósito, a diferencia de ListAsync: los llamadores de éste mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<TaxRate?> FindAsync(
        Guid tenantId,
        TaxRateId taxRateId,
        CancellationToken cancellationToken) =>
        dbContext.TaxRates.SingleOrDefaultAsync(
            taxRate => taxRate.TenantId == tenantId && taxRate.Id == taxRateId,
            cancellationToken);

    public Task<TaxRate?> FindByNameAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var pattern = EscapeLikeWildcards(name.Trim());
        return dbContext.TaxRates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                taxRate =>
                    taxRate.TenantId == tenantId &&
                    EF.Functions.ILike(taxRate.Name, pattern, LikeEscapeCharacter),
                cancellationToken);
    }

    public void Add(TaxRate taxRate) => dbContext.TaxRates.Add(taxRate);

    public void Remove(TaxRate taxRate) => dbContext.TaxRates.Remove(taxRate);
}
