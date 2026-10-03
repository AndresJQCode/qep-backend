using Microsoft.Extensions.Options;

namespace Bootstrapper.Cors;

// Falla rápido al arrancar (ValidateOnStart), igual que ForwardedHeadersSettingsValidator: un
// origen mal escrito que se ignorara dejaría a la SPA sin API, y uno demasiado ancho abriría la
// sesión a otros orígenes y desactivaría la defensa CSRF sin error y sin log.
//
// Un origen válido es exactamente lo que el navegador manda en el header Origin: https, host y
// puerto si no es el 443, en minúsculas y sin nada más. Se compara contra GetLeftPart(Authority)
// en lugar de enumerar lo prohibido, así que una barra final, un path, un query, un fragmento,
// un ":443" explícito o un host en mayúsculas no coinciden y fallan. El "*" se revisa aparte: Uri
// acepta "https://*.qcode.co" como host.
internal sealed class CorsSettingsValidator : IValidateOptions<CorsSettings>
{
    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        var failures = new List<string>();
        for (var i = 0; i < options.AllowedOrigins.Count; i++)
        {
            var origin = options.AllowedOrigins[i];
            if (!IsExactHttpsOrigin(origin))
            {
                failures.Add(
                    $"{CorsSettings.SectionName}:AllowedOrigins:{i} '{origin}' is not an exact https origin "
                    + "(e.g. https://qep.qcode.co): no wildcard, path, query, trailing slash or user info.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsExactHttpsOrigin(string? origin) =>
        !string.IsNullOrEmpty(origin)
        && !origin.Contains('*', StringComparison.Ordinal)
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.Ordinal);
}
