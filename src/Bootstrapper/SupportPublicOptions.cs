using Bootstrapper.Cors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// La sección <c>Support:PublicForm</c> (plan F4, F28 y F29), con los valores de producción por defecto en código: en local y en las
/// pruebas el enlace sale igual que en producción sin configurar nada. <see cref="AllowedOrigins"/> no tiene valor por defecto: vacía,
/// el grupo público no recibe CORS (así corren las pruebas); en Production es obligatoria (<see cref="SupportPublicFormOptionsValidator"/>).
/// </summary>
public sealed class SupportPublicFormOptions
{
    public const string SectionName = "Support:PublicForm";

    public string BaseUrl { get; set; } = "https://soporte.lulocrm.com";

    public string Hostname { get; set; } = "soporte.lulocrm.com";

    /// <summary>Los orígenes exactos de la página pública (p. ej. <c>https://soporte.lulocrm.com</c>), que llaman al grupo
    /// <c>/api/v1/public/support/…</c> sin credenciales. Mismas reglas que <c>Cors:AllowedOrigins</c>.</summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

/// <summary>El puerto de Soporte sobre la sección. Quita la barra final de <c>BaseUrl</c>, para que el enlace no salga con dos.</summary>
internal sealed class SupportPublicOptions(IOptions<SupportPublicFormOptions> options) : ISupportPublicOptions
{
    public string BaseUrl => options.Value.BaseUrl.TrimEnd('/');

    public string Hostname => options.Value.Hostname;
}

/// <summary>
/// Falla rápido al arrancar (ValidateOnStart), como <see cref="CorsSettingsValidator"/> y con sus mismas reglas por origen: https exacto,
/// sin comodín, ruta, barra final ni mayúsculas; <c>http://localhost[:puerto]</c> sólo en Development. Un origen demasiado ancho dejaría
/// a cualquier sitio leer las respuestas del grupo. Además, en Production la lista no puede venir vacía —la página quedaría sin API, sin
/// error y sin log— ni traer un token del pipeline sin resolver (<c>#{…}#</c>, que la plantilla deja literal).
/// </summary>
internal sealed class SupportPublicFormOptionsValidator(IHostEnvironment environment) : IValidateOptions<SupportPublicFormOptions>
{
    private const string Key = SupportPublicFormOptions.SectionName + ":AllowedOrigins";

    public ValidateOptionsResult Validate(string? name, SupportPublicFormOptions options)
    {
        var failures = new List<string>();
        if (environment.IsProduction() && options.AllowedOrigins.Count == 0)
        {
            failures.Add($"{Key} is required in Production: the public support page cannot call the API without it.");
        }

        for (var i = 0; i < options.AllowedOrigins.Count; i++)
        {
            var origin = options.AllowedOrigins[i];
            if (origin is not null && origin.Contains("#{", StringComparison.Ordinal))
            {
                failures.Add($"{Key}:{i} '{origin}' is an unresolved pipeline token.");
                continue;
            }

            if (!CorsSettingsValidator.IsExactHttpsOrigin(origin)
                && !(environment.IsDevelopment() && CorsSettingsValidator.IsExactHttpLocalhostOrigin(origin)))
            {
                failures.Add(
                    $"{Key}:{i} '{origin}' is not an exact https origin (e.g. https://soporte.lulocrm.com): no wildcard, path, "
                    + "query, trailing slash or user info. Only Development also accepts http://localhost[:port].");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
