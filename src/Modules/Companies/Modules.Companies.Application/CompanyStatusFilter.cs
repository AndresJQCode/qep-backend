using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// El filtro por estado del listado. Existe porque el consumidor ya lo manda:
/// <c>?status=active|inactive</c> (<c>features/companies/services/companies.api.ts</c>).
/// Ausente significa **las dos**, no "solo las activas".
/// </summary>
public enum CompanyStatusFilter
{
    Active,
    Inactive
}

public static class CompanyStatusFilterParser
{
    /// <summary>
    /// Traduce el valor crudo del query string. Un valor que no se reconoce **falla**, no se
    /// ignora: tratarlo como "sin filtro" devuelve el listado completo con un 200, y quien
    /// escribio <c>?status=activo</c> ve todas las empresas y concluye que no hay inactivas.
    /// Un filtro que miente en silencio es peor que un error.
    /// </summary>
    public static CompanyStatusFilter? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "active" => CompanyStatusFilter.Active,
            "inactive" => CompanyStatusFilter.Inactive,
            _ => throw new CompaniesDomainException(
                "companies.company.status_filter_invalid",
                "The status filter must be either 'active' or 'inactive'.")
        };
    }
}
