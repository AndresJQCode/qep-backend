namespace BuildingBlocks.Domain;

/// <summary>
/// El recorte de un texto que se congela como foto en una columna de largo fijo. Con variantes,
/// el nombre visible «{producto} – {v1} / {v2} / {v3}» (spec 2026-10-20 §3.4) puede pasar de la
/// columna <c>product_name</c>: el producto llega a 200 caracteres y cada valor de opción a 60. Se
/// decidió recortar y no ensanchar la columna (2026-10-21; handoff de variantes, §11).
///
/// Vive acá, y no copiado en cada módulo, por el mismo motivo que <c>VatIncludedLine</c>: la usan
/// Quotations (<c>QuotationProductSnapshot</c>) y Pos (<c>PosSaleLine</c>), que no se referencian
/// entre sí, y dos copias de la regla pueden divergir sin que ninguna prueba lo note.
/// </summary>
public static class SnapshotText
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Devuelve <paramref name="value"/> tal cual si cabe en <paramref name="maxLength"/>; si no, lo
    /// corta y termina en «…», contando el «…» dentro del largo. No parte un par sustituto: si el corte
    /// cae entre las dos mitades de un emoji, corta un carácter antes (la mitad suelta no es UTF-16
    /// válido, y ni Npgsql ni el PDF la pueden codificar). Quita los espacios que queden antes del «…».
    /// </summary>
    public static string Fit(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Con menos de 2 no cabe ni un carácter más el «…».
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength - Ellipsis.Length;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut).TrimEnd(), Ellipsis);
    }
}
