using BuildingBlocks.Application;
using BuildingBlocks.Domain.Taxes;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record GetTaxRateOptionsQuery(Guid TenantId) : IQuery<TaxRateOptionsDto>;

public sealed class GetTaxRateOptionsHandler(
    ITaxRateCountrySource countrySource,
    IExecutionContext executionContext)
    : IQueryHandler<GetTaxRateOptionsQuery, TaxRateOptionsDto>
{
    public async Task<TaxRateOptionsDto> HandleAsync(
        GetTaxRateOptionsQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.TaxRateRead);

        var companyCountries = await countrySource.ListCompanyCountriesAsync(query.TenantId, cancellationToken);

        // El orden es el de TaxCountries.All, no el de las empresas: la lista no cambia según cuál se creó primero.
        var countries = TaxCountries.All
            .Where(country => companyCountries.Contains(country.Country))
            .Select(country => new TaxRateCountryOptionDto(
                country.Country,
                country.TaxUnitName,
                country.TaxCurrency,
                country.Types
                    .Select(type => new TaxRateTypeOptionDto(
                        type.Type, type.Behavior.ToString(), type.AllowsFraction, type.AllowsMinimumBase))
                    .ToArray()))
            .ToArray();

        return new TaxRateOptionsDto(countries);
    }
}
