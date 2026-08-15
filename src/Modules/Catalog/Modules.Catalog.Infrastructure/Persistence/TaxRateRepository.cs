using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class TaxRateRepository(CatalogDbContext dbContext) : ITaxRateRepository
{
    // Sin ILike ni escapado de comodines, a diferencia de ProductRepository: este recurso no
    // expone búsqueda por texto. Ese es también el motivo por el que no puede repetir el defecto
    // de `?search=_` que encontró la revisión de fiabilidad de CAT-02.
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

    public void Add(TaxRate taxRate) => dbContext.TaxRates.Add(taxRate);

    public void Remove(TaxRate taxRate) => dbContext.TaxRates.Remove(taxRate);
}
