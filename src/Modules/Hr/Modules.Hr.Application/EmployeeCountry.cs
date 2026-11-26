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

    /// <summary>El cruce país–documento (null si cuadra): una sola tabla para el alta, la edición y la importación.</summary>
    public static string? DocumentMismatch(string country, HrDocumentType documentType)
    {
        var colombian = string.Equals(country, Colombia, StringComparison.Ordinal);
        if (documentType == HrDocumentType.CC && !colombian)
        {
            return "Con cédula de ciudadanía el país es Colombia.";
        }

        return documentType != HrDocumentType.CC && colombian
            ? "Con un documento extranjero el país no puede ser Colombia."
            : null;
    }

    /// <summary>FI-EMP-017 (Ley 100/1993, art. 15): sólo quien no es de acá puede quedar exento de pensión (null si cuadra).</summary>
    public static string? PensionExemptionNotAllowed(string country, bool foreignPensionExempt) =>
        foreignPensionExempt && string.Equals(country, Colombia, StringComparison.Ordinal)
            ? "Sólo un empleado de otro país puede estar exento de pensión."
            : null;

    public static void Ensure(string country, HrDocumentType documentType, bool foreignPensionExempt)
    {
        if (DocumentMismatch(country, documentType) is { } mismatch)
        {
            HrFieldErrors.Throw("country", mismatch);
        }

        if (PensionExemptionNotAllowed(country, foreignPensionExempt) is { } notAllowed)
        {
            HrFieldErrors.Throw("foreignPensionExempt", notAllowed);
        }
    }
}
