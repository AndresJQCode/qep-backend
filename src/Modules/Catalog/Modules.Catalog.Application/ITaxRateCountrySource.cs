namespace Modules.Catalog.Application;

// Puerto de Catalog hacia Companies: Catalog nunca referencia otro módulo de negocio, así que los
// países de las empresas del tenant llegan por acá y el composition root cablea el adaptador.
public interface ITaxRateCountrySource
{
    /// <summary>Los países (ISO alfa-2, mayúsculas) de las empresas del tenant, activas o no.</summary>
    Task<IReadOnlySet<string>> ListCompanyCountriesAsync(Guid tenantId, CancellationToken cancellationToken);
}
