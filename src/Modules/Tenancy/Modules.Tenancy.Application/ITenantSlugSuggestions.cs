using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §3b: el formulario del distribuidor no pide identificador; el servidor lo genera de
/// la razón social. La sugerencia es libre en el momento de leerla, no una reserva: si otra aceptación la toma primero, quien
/// llama reintenta (el reintento vive en el endpoint de aceptación, no aquí).
/// </summary>
public interface ITenantSlugSuggestions
{
    /// <returns><see cref="TenantSlugs.Normalize"/> del nombre si está libre; si no, con <c>-2</c>, <c>-3</c>… hasta uno que
    /// ningún tenant tenga y que no esté reservado (<c>TenantSlugReservations</c>), sin pasar de
    /// <see cref="TenantSlugs.MaxLength"/>.</returns>
    Task<string> SuggestAsync(string displayName, CancellationToken cancellationToken);
}

/// <summary>Las reglas de forma del slug que valida <c>Tenant.Create</c>: 3 a 63 letras o dígitos ASCII en minúscula y
/// guiones.</summary>
public static class TenantSlugs
{
    public const int MinLength = 3;

    public const int MaxLength = 63;

    /// <summary>El slug de respaldo cuando el nombre no deja al menos <see cref="MinLength"/> caracteres útiles.</summary>
    public const string Fallback = "distribuidor";

    // Spec 2026-11-19 §3b: la forma societaria suelta del final del nombre (SAS, S.A.S, SA, S.A, LTDA, LTA, con o sin puntos y
    // con coma antes) no entra al slug. Sólo cuenta una palabra entera: antes tiene que haber un espacio o un signo, así que
    // «Casas» o «Pesa» no pierden su final.
    private static readonly Regex CorporateSuffix = new(
        @"(?<=[\s,;.\-])(?:s\.?\s*a\.?\s*s|s\.?\s*a|ltda|lta)\.?[\s,;.\-]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Minúsculas y sin tildes; sin la forma societaria del final (<c>S.A.S.</c>, <c>LTDA</c>…); todo lo que no sea letra o
    /// dígito ASCII pasa a <c>-</c>, sin guiones repetidos ni en los extremos, con tope de <see cref="MaxLength"/>. Si queda
    /// de menos de <see cref="MinLength"/> caracteres devuelve <see cref="Fallback"/>.
    /// </summary>
    public static string Normalize(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var decomposed = WithoutCorporateSuffix(displayName).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = Cut(builder.ToString(), MaxLength);
        return slug.Length < MinLength ? Fallback : slug;
    }

    // Se repite por si hay más de una («Andina Ltda. S.A.S.»). Si lo único que queda es la forma societaria, el nombre se deja
    // entero: «SAS» no pasa a ser vacío.
    private static string WithoutCorporateSuffix(string displayName)
    {
        var current = displayName;
        while (true)
        {
            var stripped = CorporateSuffix.Replace(current, string.Empty);
            if (stripped.Length == current.Length || !stripped.Any(char.IsLetterOrDigit))
            {
                return current;
            }

            current = stripped;
        }
    }

    /// <summary>El slug con el sufijo numérico, recortando la base para no pasar de <see cref="MaxLength"/>.</summary>
    public static string WithSuffix(string slug, int number)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        var suffix = "-" + number.ToString(CultureInfo.InvariantCulture);
        return Cut(slug, MaxLength - suffix.Length) + suffix;
    }

    private static string Cut(string value, int maxLength) =>
        (value.Length > maxLength ? value[..maxLength] : value).Trim('-');
}
