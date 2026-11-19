using System.Globalization;
using System.Text;

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

    /// <summary>
    /// Minúsculas y sin tildes; todo lo que no sea letra o dígito ASCII pasa a <c>-</c>, sin guiones repetidos ni en los
    /// extremos, con tope de <see cref="MaxLength"/>. Si queda de menos de <see cref="MinLength"/> caracteres devuelve
    /// <see cref="Fallback"/>.
    /// </summary>
    public static string Normalize(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var decomposed = displayName.Normalize(NormalizationForm.FormD);
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
