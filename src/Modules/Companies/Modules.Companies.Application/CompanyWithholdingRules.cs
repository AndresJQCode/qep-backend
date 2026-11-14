using BuildingBlocks.Domain.Taxes;
using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// Qué tasas municipales puede guardar una empresa (spec ReteICA por empresa §7.3). Vive en Application y no en el agregado
/// porque necesita el catálogo, que llega por <see cref="ICompanyTaxRateLookup"/>.
/// </summary>
public static class CompanyWithholdingRules
{
    /// <summary>
    /// Cada id existe en el tenant, retiene, es de un tipo municipal y del país de la empresa, y está activo —salvo que la
    /// empresa ya lo tuviera guardado (A16)—; si no, <c>companies.company.withholding_tax_rate_invalid</c>. Dos tasas del
    /// mismo tipo y municipio son <c>companies.company.withholding_municipality_repeated</c>. El municipio propio de la
    /// empresa no restringe: puede tener tarifa donde presta servicios.
    /// </summary>
    public static void Validate(
        IReadOnlyDictionary<Guid, CompanyTaxRateRef> found,
        IReadOnlyCollection<Guid> requested,
        IReadOnlyCollection<Guid> alreadyAssigned,
        string companyCountry)
    {
        var country = TaxCountries.Find(companyCountry)?.Country;
        var rates = new List<CompanyTaxRateRef>(requested.Count);
        foreach (var id in requested.Distinct())
        {
            if (!found.TryGetValue(id, out var rate)
                || !rate.Withholds
                || country is null
                || !string.Equals(rate.Country, country, StringComparison.Ordinal)
                || !TaxCountries.IsMunicipal(rate.Country, rate.Type)
                || (!rate.IsActive && !alreadyAssigned.Contains(id)))
            {
                throw new CompaniesDomainException(
                    "companies.company.withholding_tax_rate_invalid",
                    $"The tax rate '{id}' is not an active municipal withholding rate of the company's country.");
            }

            rates.Add(rate);
        }

        var repeated = rates
            .GroupBy(rate => (rate.Type, rate.CityId))
            .FirstOrDefault(group => group.Count() > 1);
        if (repeated is not null)
        {
            throw new CompaniesDomainException(
                "companies.company.withholding_municipality_repeated",
                $"The company has more than one {repeated.Key.Type} rate for the same municipality.");
        }
    }
}
