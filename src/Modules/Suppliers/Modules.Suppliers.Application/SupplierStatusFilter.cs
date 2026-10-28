using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Application;

/// <summary>
/// El filtro por estado de la lista: <c>?status=active|inactive</c>. Ausente significa **las
/// dos**, no «sólo las activas».
/// </summary>
public enum SupplierStatusFilter
{
    Active,
    Inactive
}

public static class SupplierStatusFilterParser
{
    /// <summary>
    /// Traduce el valor crudo del query string. Un valor que no se reconoce **falla**, no se
    /// ignora: tratarlo como «sin filtro» devuelve la lista completa con un 200, y quien escribió
    /// <c>?status=activo</c> ve todos los proveedores y concluye que no hay inactivos.
    /// </summary>
    public static SupplierStatusFilter? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "active" => SupplierStatusFilter.Active,
            "inactive" => SupplierStatusFilter.Inactive,
            _ => throw new SuppliersDomainException(
                "suppliers.supplier.status_filter_invalid",
                "The status filter must be either 'active' or 'inactive'.")
        };
    }
}
