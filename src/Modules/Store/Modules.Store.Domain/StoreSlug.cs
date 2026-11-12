using System.Text.RegularExpressions;

namespace Modules.Store.Domain;

public static partial class StoreSlug
{
    public const int MinLength = 3;

    public const int MaxLength = 40;

    // Evitan que {slug}.lulocrm.com pise la infraestructura propia (api, mail, tunnel...).
    public static readonly IReadOnlyCollection<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "www", "app", "api", "admin", "mail", "smtp", "ftp", "static", "cdn", "docs",
        "status", "blog", "help", "soporte", "tienda", "shop", "qep", "qep-api", "tunnel",
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();

    /// <summary>Recorta y pasa a minúsculas antes de validar: «Tienda» llega a «tienda» y de ahí a reservado.</summary>
    public static bool TryNormalize(string? raw, out string slug, out string? errorCode)
    {
        slug = (raw ?? string.Empty).Trim().ToLowerInvariant();

        if (!Format().IsMatch(slug))
        {
            errorCode = StoreErrorCodes.SlugInvalid;
            return false;
        }

        if (Reserved.Contains(slug))
        {
            errorCode = StoreErrorCodes.SlugReserved;
            return false;
        }

        errorCode = null;
        return true;
    }
}
