using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-09 §9: en <c>Production</c> exige las cinco claves; sin ellas el pod no arranca, a
/// propósito, como <c>SecretProtectionOptionsValidator</c>. La forma de la versión (<c>^v\d+\.\d+$</c>)
/// se exige en todo ambiente cuando viene. El override de la WABA, cuando viene, es una URL https absoluta de hasta 200 caracteres
/// (el límite de Meta) y exige el token de verificación, porque Meta hace el desafío GET con él; en <c>Production</c> un token del
/// pipeline sin resolver cuenta como ausente: una variable temporal que no está en el grupo no puede tumbar el pod. Ningún mensaje
/// lleva un valor: nombran la clave.
/// </summary>
internal sealed partial class MetaAppOptionsValidator(IHostEnvironment environment) : IValidateOptions<MetaAppOptions>
{
    [GeneratedRegex(@"^v\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();

    private const int MaxOverrideCallbackUrlLength = 200;

    public ValidateOptionsResult Validate(string? name, MetaAppOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.GraphApiVersion) && !VersionShape().IsMatch(options.GraphApiVersion.Trim()))
        {
            failures.Add($"{MetaAppOptions.SectionName}:GraphApiVersion must look like v24.0.");
        }

        var overrideUrl = environment.IsProduction() ? options.ResolvedWebhookOverrideCallbackUrl() : options.WebhookOverrideCallbackUrl?.Trim();
        if (!string.IsNullOrEmpty(overrideUrl))
        {
            ValidateOverride(overrideUrl, options.WebhookVerifyToken, failures);
        }

        if (environment.IsProduction())
        {
            Require(options.AppId, nameof(MetaAppOptions.AppId), failures);
            Require(options.ConfigId, nameof(MetaAppOptions.ConfigId), failures);
            Require(options.GraphApiVersion, nameof(MetaAppOptions.GraphApiVersion), failures);
            Require(options.AppSecret, nameof(MetaAppOptions.AppSecret), failures);
            Require(options.WebhookVerifyToken, nameof(MetaAppOptions.WebhookVerifyToken), failures);
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateOverride(string url, string? verifyToken, List<string> failures)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || url.Length > MaxOverrideCallbackUrlLength)
        {
            failures.Add($"{MetaAppOptions.SectionName}:WebhookOverrideCallbackUrl must be an absolute https URL of at most {MaxOverrideCallbackUrlLength} characters, like https://qep-api.qcode.co/api/webhooks/whatsapp.");
        }

        if (string.IsNullOrWhiteSpace(verifyToken) || MetaAppOptions.IsUnresolvedPipelineToken(verifyToken))
        {
            failures.Add($"{MetaAppOptions.SectionName}:WebhookVerifyToken is required with WebhookOverrideCallbackUrl: Meta verifies the callback with it.");
        }
    }

    private static void Require(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{MetaAppOptions.SectionName}:{key} is required in Production: without it WhatsApp cannot be connected nor its webhook verified.");
        }
    }
}
