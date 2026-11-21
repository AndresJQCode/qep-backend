using Microsoft.Extensions.Options;

namespace Modules.Coordinadora.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-21 §8: la sección <c>Coordinadora</c>. Las dos URL de los servidores (producción por defecto; el sandbox se usa cambiándolas
/// en el ambiente, no por conexión) y el <c>xmlns</c> del cuerpo SOAP de guías (V-1; vacío = <see cref="GuidesUrl"/>, que es lo que arma el
/// cliente PHP de referencia sin WSDL). Nada es secreto: van en el ConfigMap. La sección la lee también el probador de conexiones de
/// Integrations (<c>CoordinadoraTesterOptions</c>), que no ve este módulo.
/// </summary>
public sealed class CoordinadoraOptions
{
    public const string SectionName = "Coordinadora";

    public const string DefaultAgsUrl = "https://ws.coordinadora.com/ags/1.5/server.php";

    public const string DefaultGuidesUrl = "https://guias.coordinadora.com/ws/guias/1.6/server.php";

    /// <summary>El AGS (cotizador, recaudos, recogidas), JSON-RPC 2.0. Vacía vale la de producción.</summary>
    public string? AgsUrl { get; set; } = DefaultAgsUrl;

    /// <summary>El servidor de guías, SOAP 1.2. Vacía vale la de producción.</summary>
    public string? GuidesUrl { get; set; } = DefaultGuidesUrl;

    /// <summary>El espacio de nombres del elemento de la operación SOAP. Vacío: la URL de guías.</summary>
    public string? GuidesNamespace { get; set; }

    // Métodos y no propiedades: ConfigurationExampleTests toma toda propiedad pública como una clave que el ejemplo debe documentar.
    public Uri AgsUri() => new(string.IsNullOrWhiteSpace(AgsUrl) ? DefaultAgsUrl : AgsUrl.Trim());

    public Uri GuidesUri() => new(string.IsNullOrWhiteSpace(GuidesUrl) ? DefaultGuidesUrl : GuidesUrl.Trim());

    public string EffectiveGuidesNamespace() => string.IsNullOrWhiteSpace(GuidesNamespace) ? GuidesUri().ToString() : GuidesNamespace.Trim();
}

/// <summary>Spec 2026-11-21 §8: falla rápido al arrancar (<c>ValidateOnStart</c>). Las dos URL, si vienen, son absolutas y HTTPS en todo
/// ambiente: las claves viajan en el cuerpo. Ningún mensaje lleva el valor: nombran la clave.</summary>
internal sealed class CoordinadoraOptionsValidator : IValidateOptions<CoordinadoraOptions>
{
    public ValidateOptionsResult Validate(string? name, CoordinadoraOptions options)
    {
        var failures = new List<string>();
        Check(options.AgsUrl, "AgsUrl", failures);
        Check(options.GuidesUrl, "GuidesUrl", failures);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void Check(string? value, string key, List<string> failures)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{CoordinadoraOptions.SectionName}:{key} must be an absolute HTTPS URL: the credentials travel in the body.");
        }
    }
}
