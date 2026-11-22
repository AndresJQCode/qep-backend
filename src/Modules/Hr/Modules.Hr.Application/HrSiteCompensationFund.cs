using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Regla de la caja de compensación de una sede (delta 2026-11-21): es un aporte parafiscal colombiano, así que sólo aplica si la
/// empresa de la sede es de Colombia, y ahí debe ser un nombre exacto del catálogo. Vive aparte del validador porque necesita el
/// país de la empresa, que sólo se conoce con lo cargado.
/// </summary>
public static class HrSiteCompensationFund
{
    public const string ColombiaCode = "CO";
    public const string NotInCatalogMessage = "Elige una caja de compensación de la lista.";
    public const string ColombiaOnlyMessage = "La caja de compensación sólo aplica a empresas de Colombia.";

    /// <summary>El mensaje del 422 sobre <c>compensationFund</c>, o <c>null</c> si el valor es válido.</summary>
    public static string? Check(string companyCountry, string? fund)
    {
        if (string.IsNullOrWhiteSpace(fund))
        {
            return null;
        }

        if (!string.Equals(companyCountry, ColombiaCode, StringComparison.OrdinalIgnoreCase))
        {
            return ColombiaOnlyMessage;
        }

        return HrCompensationFunds.Contains(fund) ? null : NotInCatalogMessage;
    }

    internal static void Ensure(string companyCountry, string? fund)
    {
        if (Check(companyCountry, fund) is { } message)
        {
            HrFieldErrors.Throw("compensationFund", message);
        }
    }
}
