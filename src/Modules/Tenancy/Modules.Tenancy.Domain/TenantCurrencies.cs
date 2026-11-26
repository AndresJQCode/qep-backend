namespace Modules.Tenancy.Domain;

/// <summary>
/// La moneda con la que nace un tenant según su país (decisión del owner, 2026-11-23). El catálogo de monedas sólo tiene
/// COP, USD y EUR, así que: Colombia, COP; los miembros del euro, EUR; cualquier otro país, USD. Sólo se usa al crear el
/// tenant: después la moneda es del tenant y se cambia en «Región y formato».
/// </summary>
public static class TenantCurrencies
{
    // ISO-3166-1 alpha-2 de los países que usan el euro (la eurozona al 2026-01-01, con Bulgaria).
    private static readonly HashSet<string> Eurozone = new(StringComparer.Ordinal)
    {
        "AT", "BE", "BG", "CY", "DE", "EE", "ES", "FI", "FR", "GR", "HR", "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PT", "SI", "SK",
    };

    public static string ForCountry(string country)
    {
        var code = country.Trim().ToUpperInvariant();
        return code == "CO" ? "COP" : Eurozone.Contains(code) ? "EUR" : "USD";
    }
}
