using FluentValidation;
using FluentValidation.Results;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-11-10 §5: el parámetro <c>contacts</c> del listado y de la exportación. <c>Default</c> es «no lo mandaron».</summary>
public enum CustomerContactsFilter
{
    Default,
    Include,
    Exclude
}

/// <summary>
/// Quién decide si el listado y la exportación incluyen a los contactos de una empresa. Una sola regla para los dos, porque se
/// exporta lo que se está viendo en la grilla.
/// </summary>
internal static class CustomerContactsRule
{
    public const string ParameterName = "contacts";

    public const string Message = "contacts debe ser include o exclude.";

    /// <summary>Sin distinguir mayúsculas (G7). <c>null</c> es «no lo mandaron»; cualquier otro valor que no sea
    /// <c>include</c> o <c>exclude</c> (vacío incluido) no se reconoce.</summary>
    public static bool TryParse(string? value, out CustomerContactsFilter filter)
    {
        filter = CustomerContactsFilter.Default;
        if (value is null)
        {
            return true;
        }

        if (string.Equals(value, "include", StringComparison.OrdinalIgnoreCase))
        {
            filter = CustomerContactsFilter.Include;
            return true;
        }

        if (string.Equals(value, "exclude", StringComparison.OrdinalIgnoreCase))
        {
            filter = CustomerContactsFilter.Exclude;
            return true;
        }

        return false;
    }

    /// <summary>Para los caminos sin validador (la exportación): el mismo 422 con la clave <c>contacts</c>.</summary>
    public static CustomerContactsFilter ParseOrThrow(string? value) =>
        TryParse(value, out var filter)
            ? filter
            : throw new ValidationException([new ValidationFailure(ParameterName, Message)]);

    /// <summary>
    /// <c>Include</c> y <c>Exclude</c> mandan. Sin ellos, los contactos aparecen sólo cuando hay una búsqueda de verdad: una caja
    /// vacía o en blanco no busca nada (Review Focus 1), y la del teléfono sólo cuenta si trae algún dígito, que es cuando el
    /// repositorio la convierte en filtro.
    /// </summary>
    public static bool IncludeContacts(
        CustomerContactsFilter filter,
        string? search,
        string? name,
        string? identificationNumber,
        string? cuc,
        string? phone) => filter switch
        {
            CustomerContactsFilter.Include => true,
            CustomerContactsFilter.Exclude => false,
            _ => !string.IsNullOrWhiteSpace(search)
                || !string.IsNullOrWhiteSpace(name)
                || !string.IsNullOrWhiteSpace(identificationNumber)
                || !string.IsNullOrWhiteSpace(cuc)
                || (phone is not null && phone.Any(char.IsAsciiDigit))
        };
}
