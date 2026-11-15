using Modules.Catalog.Application;
using Modules.Companies.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de empresas de <c>Companies</c> al puerto con el que <c>catalog</c> sabe si una tasa municipal está
/// asignada a alguna empresa (spec ReteICA por empresa §7.2). No decide nada: sólo traduce.
/// </summary>
internal sealed class TaxRateCompanyUsage(ICompanyRepository repository) : ITaxRateCompanyUsage
{
    public Task<bool> AnyCompanyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken) =>
        repository.AnyWithTaxRateAsync(tenantId, taxRateId, cancellationToken);
}
