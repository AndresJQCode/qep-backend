using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Bootstrapper;

/// <summary>Los códigos DANE de una ciudad y de su departamento, tal como Siigo los pide (<c>city_code</c> de cinco dígitos y
/// <c>state_code</c> de dos; Bogotá <c>11001</c> / <c>11</c>).</summary>
internal sealed record SiigoCityCodes(string CityCode, string StateCode);

/// <summary>El puerto que <see cref="SiigoOrderSource"/> usa para pasar la ciudad de un pedido a códigos DANE. Vive en Bootstrapper,
/// como <c>CompanyGeographyLookup</c>: ni Siigo ni Quotations referencian a Geography.</summary>
internal interface ISiigoGeographyLookup
{
    Task<SiigoCityCodes?> FindCodesAsync(Guid cityId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-26: la ciudad escrita de un país que no es Colombia, contra la lista de Siigo. Sólo lee.
    /// <paramref name="countryCode"/> llega recortado y en mayúscula.</summary>
    Task<SiigoForeignCityLookup> FindForeignCodesAsync(string countryCode, string cityText, CancellationToken cancellationToken);
}

/// <summary>Los tres códigos de la lista de Siigo para una ciudad del exterior, tal cual vienen (<c>Us</c>, <c>09</c>, <c>1</c>).
/// <see cref="CountryCode"/> es el de Siigo, no el de QEP.</summary>
internal sealed record SiigoForeignCityCodes(string CountryCode, string StateCode, string CityCode);

/// <summary><see cref="CountryInCatalog"/> es <c>false</c> cuando el país no está en el catálogo de Siigo (no hay ciudad que un operador
/// pueda elegir y no se anota). <see cref="Codes"/> es <c>null</c> si no coincidió una sola ciudad.</summary>
internal sealed record SiigoForeignCityLookup(bool CountryInCatalog, SiigoForeignCityCodes? Codes);

/// <summary>Lee <c>City.DivipolaCode</c> y <c>Department.DivipolaCode</c> de Geography. No decide nada: una ciudad que no existe o sin
/// código es <c>null</c>, y el armador lo reporta como <c>customer_city_missing</c>. Las ciudades del exterior las resuelve
/// <see cref="IForeignBillingCityResolver"/>, que sólo lee: anotar en la bandeja es otra clase (<c>SiigoForeignCityInbox</c>).</summary>
internal sealed class SiigoGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository,
    IForeignBillingCityResolver foreignResolver) : ISiigoGeographyLookup
{
    public async Task<SiigoForeignCityLookup> FindForeignCodesAsync(
        string countryCode, string cityText, CancellationToken cancellationToken)
    {
        var resolution = await foreignResolver.ResolveAsync(countryCode, cityText, cancellationToken);
        return resolution.Kind switch
        {
            ForeignCityResolutionKind.CountryNotInCatalog => new SiigoForeignCityLookup(false, null),
            ForeignCityResolutionKind.Matched when resolution.Codes is { } codes => new SiigoForeignCityLookup(
                true, new SiigoForeignCityCodes(codes.SiigoCountryCode, codes.StateCode, codes.CityCode)),
            _ => new SiigoForeignCityLookup(true, null),
        };
    }

    public async Task<SiigoCityCodes?> FindCodesAsync(Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cityRepository.FindAsync(new CityId(cityId), cancellationToken);
        if (city is null || string.IsNullOrWhiteSpace(city.DivipolaCode))
        {
            return null;
        }

        var department = await departmentRepository.FindAsync(city.DepartmentId, cancellationToken);
        return department is null || string.IsNullOrWhiteSpace(department.DivipolaCode)
            ? null
            : new SiigoCityCodes(city.DivipolaCode.Trim(), department.DivipolaCode.Trim());
    }
}
