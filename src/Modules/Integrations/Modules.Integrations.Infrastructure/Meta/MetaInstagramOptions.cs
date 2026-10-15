using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// La sección <c>Meta:Instagram</c> (spec 2026-10-14 §9, V-1 cerrada): el caso de uso Instagram de la app de Meta
/// tiene su propio <c>AppId</c> y <c>AppSecret</c>, distintos de <c>Meta:App</c>. <c>AppId</c>, la versión y los
/// orígenes permitidos del <c>redirect_uri</c> son públicos (ConfigMap); <c>AppSecret</c> es secreto (user-secrets
/// en local, Secret en k8s). <b>Vacío = ausente</b>, como <see cref="MetaAppOptions"/>.
/// </summary>
public sealed class MetaInstagramOptions
{
    public const string SectionName = "Meta:Instagram";

    public string? AppId { get; set; }

    public string? AppSecret { get; set; }

    public string GraphApiVersion { get; set; } = MetaAppOptions.DefaultGraphApiVersion;

    /// <summary>§11: los orígenes (esquema, host y puerto) desde los que el SPA puede volver con el <c>code</c>. En
    /// local <c>http://localhost:3002</c>; en producción el del SPA, desde el ConfigMap.</summary>
    public List<string> AllowedRedirectOrigins { get; set; } = [];

    /// <summary>D-M3 aplicado a Instagram: con las dos claves el proveedor sale en el catálogo; sin ellas (sólo fuera
    /// de Production) el módulo arranca igual.</summary>
    public bool IsConfigured() => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>En <c>Production</c> exige <c>AppId</c>, <c>AppSecret</c> y al menos un origen permitido; la forma de la
/// versión y de cada origen (URI absoluta http(s) sin ruta) se exige en todo ambiente cuando vienen. Ningún mensaje
/// lleva un valor.</summary>
internal sealed partial class MetaInstagramOptionsValidator(IHostEnvironment environment) : IValidateOptions<MetaInstagramOptions>
{
    [GeneratedRegex(@"^v\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();

    public ValidateOptionsResult Validate(string? name, MetaInstagramOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.GraphApiVersion) && !VersionShape().IsMatch(options.GraphApiVersion.Trim()))
        {
            failures.Add($"{MetaInstagramOptions.SectionName}:GraphApiVersion must look like v24.0.");
        }

        for (var index = 0; index < options.AllowedRedirectOrigins.Count; index++)
        {
            if (!IsOrigin(options.AllowedRedirectOrigins[index]))
            {
                failures.Add($"{MetaInstagramOptions.SectionName}:AllowedRedirectOrigins:{index} must be an absolute http(s) origin without a path, like https://qep.qcode.co.");
            }
        }

        if (environment.IsProduction())
        {
            Require(options.AppId, nameof(MetaInstagramOptions.AppId), failures);
            Require(options.AppSecret, nameof(MetaInstagramOptions.AppSecret), failures);
            if (options.AllowedRedirectOrigins.Count == 0)
            {
                failures.Add($"{MetaInstagramOptions.SectionName}:AllowedRedirectOrigins needs at least one origin in Production: without it no Instagram account can be connected.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    internal static bool IsOrigin(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.AbsolutePath is "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static void Require(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{MetaInstagramOptions.SectionName}:{key} is required in Production: without it no Instagram account can be connected nor its webhook verified.");
        }
    }
}

/// <summary>Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.</summary>
internal sealed class InstagramAppSettings(IOptions<MetaInstagramOptions> options) : IInstagramAppSettings
{
    private readonly MetaInstagramOptions settings = options.Value;

    private readonly HashSet<string> origins = options.Value.AllowedRedirectOrigins
        .Where(MetaInstagramOptionsValidator.IsOrigin)
        .Select(origin => new Uri(origin.Trim(), UriKind.Absolute).GetLeftPart(UriPartial.Authority))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => settings.IsConfigured();

    public string? AppId => string.IsNullOrWhiteSpace(settings.AppId) ? null : settings.AppId.Trim();

    public string GraphApiVersion =>
        string.IsNullOrWhiteSpace(settings.GraphApiVersion) ? MetaAppOptions.DefaultGraphApiVersion : settings.GraphApiVersion.Trim();

    public bool IsAllowedRedirect(Uri redirectUri) => origins.Contains(redirectUri.GetLeftPart(UriPartial.Authority));
}
