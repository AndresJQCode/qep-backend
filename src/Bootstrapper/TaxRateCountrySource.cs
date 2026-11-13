using Modules.Catalog.Application;
using Modules.Companies.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de empresas de <c>Companies</c> al puerto con el que <c>catalog</c> verifica el país
/// de una tasa y arma las opciones de la pantalla. No decide nada: sólo traduce.
/// </summary>
internal sealed class TaxRateCountrySource(ICompanyRepository repository) : ITaxRateCountrySource
{
    public Task<IReadOnlySet<string>> ListCompanyCountriesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        repository.ListCountriesAsync(tenantId, cancellationToken);
}
