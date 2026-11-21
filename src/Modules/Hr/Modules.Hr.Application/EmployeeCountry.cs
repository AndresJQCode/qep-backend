using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Una sola tabla de la regla del país del empleado (nacionalidad): la usan el alta, la edición y la importación. El validador de
/// forma sólo mira que sea un código de dos letras; lo demás depende del país efectivo, que puede salir del tenant.
/// </summary>
internal static class EmployeeCountry
{
    public const string Colombia = "CO";

    /// <summary>Recorta y pasa a mayúsculas, como <c>CompanyWriteRules.NormalizeCountry</c>; en blanco es «sin país».</summary>
    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>El país pedido; si no hay, el del tenant; si el tenant no tiene fila, Colombia.</summary>
    public static string Effective(string? requested, string? tenantCountry) =>
        Normalize(requested) ?? Normalize(tenantCountry) ?? Colombia;

    /// <summary>Fuera del catálogo de Geografía es 422 sobre <c>country</c>. El país del tenant no se revalida: no lo eligió el usuario.</summary>
    public static void EnsureKnown(string country, HrCountryIndex countries)
    {
        if (!countries.IsKnown(country))
        {
            HrFieldErrors.Throw("country", "Elige un país de la lista.");
        }
    }

    public static void Ensure(string country, HrDocumentType documentType, bool foreignPensionExempt)
    {
        var colombian = string.Equals(country, Colombia, StringComparison.Ordinal);
        if (documentType == HrDocumentType.CC && !colombian)
        {
            HrFieldErrors.Throw("country", "Con cédula de ciudadanía el país es Colombia.");
        }

        if (documentType != HrDocumentType.CC && colombian)
        {
            HrFieldErrors.Throw("country", "Con un documento extranjero el país no puede ser Colombia.");
        }

        // FI-EMP-017 (Ley 100/1993, art. 15): sólo quien no es de acá puede quedar exento de pensión.
        if (foreignPensionExempt && colombian)
        {
            HrFieldErrors.Throw("foreignPensionExempt", "Sólo un empleado de otro país puede estar exento de pensión.");
        }
    }
}
