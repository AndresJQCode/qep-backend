using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.Infrastructure.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.2: en <c>Production</c> exige <c>ClientId</c>, <c>ClientSecret</c>, <c>WebhookCallbackUrl</c> y al
/// menos un origen; sin ellos el pod no arranca, a propósito, como con Meta: una app a medio configurar dejaría
/// instalar tiendas cuyos webhooks nadie puede verificar. En todo ambiente valida, cuando vienen, la forma de
/// <c>ApiVersion</c> (<c>^\d{4}-(01|04|07|10)$</c>, las versiones trimestrales de Shopify), de cada origen y de la URL
/// (https absoluta: Shopify no entrega webhooks a http). Ningún mensaje lleva un valor: nombran la clave.
/// </summary>
internal sealed partial class ShopifyAppOptionsValidator(IHostEnvironment environment) : IValidateOptions<ShopifyAppOptions>
{
    [GeneratedRegex(@"^\d{4}-(01|04|07|10)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();

    public ValidateOptionsResult Validate(string? name, ShopifyAppOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ApiVersion) && !VersionShape().IsMatch(options.ApiVersion.Trim()))
        {
            failures.Add($"{ShopifyAppOptions.SectionName}:ApiVersion must look like 2026-10 (a quarterly Shopify version: -01, -04, -07 or -10).");
        }

        for (var index = 0; index < options.AllowedRedirectOrigins.Count; index++)
        {
            if (!MetaInstagramOptionsValidator.IsOrigin(options.AllowedRedirectOrigins[index]))
            {
                failures.Add($"{ShopifyAppOptions.SectionName}:AllowedRedirectOrigins:{index} must be an absolute http(s) origin without a path, like https://qep.qcode.co.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.WebhookCallbackUrl) && !IsHttpsUrl(options.WebhookCallbackUrl))
        {
            failures.Add($"{ShopifyAppOptions.SectionName}:WebhookCallbackUrl must be an absolute https URL, like https://qep-api.qcode.co/api/webhooks/shopify.");
        }

        if (environment.IsProduction())
        {
            Require(options.ClientId, nameof(ShopifyAppOptions.ClientId), failures);
            Require(options.ClientSecret, nameof(ShopifyAppOptions.ClientSecret), failures);
            Require(options.WebhookCallbackUrl, nameof(ShopifyAppOptions.WebhookCallbackUrl), failures);
            if (options.AllowedRedirectOrigins.Count == 0)
            {
                failures.Add($"{ShopifyAppOptions.SectionName}:AllowedRedirectOrigins needs at least one origin in Production: without it no Shopify store can be installed.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    internal static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static void Require(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{ShopifyAppOptions.SectionName}:{key} is required in Production: without it no Shopify store can be installed nor its webhooks verified.");
        }
    }
}

/// <summary>Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.</summary>
internal sealed class ShopifyAppSettings(IOptions<ShopifyAppOptions> options) : IShopifyAppSettings
{
    private readonly ShopifyAppOptions settings = options.Value;

    private readonly HashSet<string> origins = options.Value.AllowedRedirectOrigins
        .Where(MetaInstagramOptionsValidator.IsOrigin)
        .Select(origin => new Uri(origin.Trim(), UriKind.Absolute).GetLeftPart(UriPartial.Authority))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => settings.IsConfigured();

    public string? ClientId => string.IsNullOrWhiteSpace(settings.ClientId) ? null : settings.ClientId.Trim();

    public string? ClientSecret => string.IsNullOrWhiteSpace(settings.ClientSecret) ? null : settings.ClientSecret.Trim();

    public string ApiVersion =>
        string.IsNullOrWhiteSpace(settings.ApiVersion) ? ShopifyAppOptions.DefaultApiVersion : settings.ApiVersion.Trim();

    public IReadOnlyList<string> Scopes { get; } =
        (string.IsNullOrWhiteSpace(options.Value.Scopes) ? ShopifyAppOptions.DefaultScopes : options.Value.Scopes)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public Uri? WebhookCallbackUrl { get; } =
        ShopifyAppOptionsValidator.IsHttpsUrl(options.Value.WebhookCallbackUrl)
            ? new Uri(options.Value.WebhookCallbackUrl!.Trim(), UriKind.Absolute)
            : null;

    public bool IsRedirectAllowed(Uri redirectUri) => origins.Contains(redirectUri.GetLeftPart(UriPartial.Authority));
}
