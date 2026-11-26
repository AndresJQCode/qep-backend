namespace BuildingBlocks.Domain.Localization;

/// <summary>
/// Los idiomas en que el producto le habla a una persona (spec 2026-11-22, i18n). Es el **idioma**
/// del texto, no el formato regional: los separadores y la fecha siguen siendo
/// <c>Tenant.NumberFormat</c>/<c>Tenant.DateFormat</c>, y <c>Tenant.DefaultCulture</c> (<c>es-CO</c>)
/// no se toca. Por eso Latinoamérica es <c>es-419</c> (BCP 47) y no <c>es-CO</c>.
///
/// <para>Vive en BuildingBlocks porque Tenancy, Identity y Notifications lo validan y no se ven
/// entre sí. El frontend tiene el diccionario de cada uno; agregar un idioma acá sin su diccionario
/// allá deja la pantalla en el idioma por defecto.</para>
/// </summary>
public static class SupportedLanguages
{
    public const string LatinAmericanSpanish = "es-419";

    public const string SpainSpanish = "es-ES";

    /// <summary>
    /// Los tenants de hoy son colombianos y los textos ya están escritos en español latinoamericano,
    /// así que quien nunca eligió idioma no ve ningún cambio (owner, 2026-11-22).
    /// </summary>
    public const string Default = LatinAmericanSpanish;

    /// <summary>En el orden en que la pantalla los ofrece.</summary>
    public static IReadOnlyList<string> All { get; } = [LatinAmericanSpanish, SpainSpanish];

    /// <summary>
    /// Acepta un valor del catálogo sin importar mayúsculas ni espacios y lo devuelve con la forma
    /// canónica. Nada más: <c>es</c> o <c>es-CO</c> no se «aproximan» a un idioma, porque esa
    /// decisión sería del backend y el diccionario es del frontend.
    /// </summary>
    public static bool TryNormalize(string? value, out string language)
    {
        var trimmed = value?.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                language = candidate;
                return true;
            }
        }

        language = Default;
        return false;
    }

    /// <summary>
    /// El idioma con que se le habla a una persona: el que eligió si eligió uno válido; si no, el de su
    /// tenant; si el tenant no tiene (sin fila, como el tenant del stub), el por defecto. Es la única
    /// regla de resolución: <c>/auth/preferences</c>, <c>/auth/me</c> y los correos la comparten.
    /// </summary>
    public static string Effective(string? chosen, string? tenantLanguage) =>
        TryNormalize(chosen, out var language) ? language : OrDefault(tenantLanguage);

    /// <summary>Para leer lo guardado: un valor que ya no está en el catálogo cae al idioma por defecto.</summary>
    public static string OrDefault(string? value) =>
        TryNormalize(value, out var language) ? language : Default;
}
