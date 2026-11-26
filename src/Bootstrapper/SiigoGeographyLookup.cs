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
}

/// <summary>Lee <c>City.DivipolaCode</c> y <c>Department.DivipolaCode</c> de Geography. No decide nada: una ciudad que no existe o sin
/// código es <c>null</c>, y el armador lo reporta como <c>customer_city_missing</c>.</summary>
internal sealed class SiigoGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : ISiigoGeographyLookup
{
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
