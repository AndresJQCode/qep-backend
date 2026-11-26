using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-18 §8: falla rápido al arrancar (<c>ValidateOnStart</c>). En <c>Production</c> exige <c>Siigo:PartnerId</c>: sin él, Siigo
/// rechaza toda llamada y es más barato un pod que no arranca que descubrirlo con la primera factura. Un token del pipeline sin
/// resolver (<c>#{…}#</c>, la variable falta en <c>Backend-prod</c>) cuenta como ausente: no vacío, pasaría por configurado. Con valor, en
/// todo ambiente: de 3 a 100 caracteres alfanuméricos. <c>Siigo:BaseUrl</c>, si viene, es absoluta y HTTPS en todo ambiente: la clave
/// de acceso viaja en el cuerpo de <c>/auth</c> (el probador de Integrations la usa igual, sin validarla). Ningún mensaje lleva el
/// valor: nombran la clave.
/// </summary>
internal sealed class SiigoOptionsValidator(IHostEnvironment environment) : IValidateOptions<SiigoOptions>
{
    public const int PartnerIdMinLength = 3;
    public const int PartnerIdMaxLength = 100;

    public ValidateOptionsResult Validate(string? name, SiigoOptions options)
    {
        var failures = new List<string>();

        var baseUrl = options.BaseUrl?.Trim();
        if (!string.IsNullOrEmpty(baseUrl)
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{SiigoOptions.SectionName}:BaseUrl must be an absolute HTTPS URL: the access key travels in the body of /auth.");
        }

        var partnerId = options.PartnerId?.Trim();
        if (string.IsNullOrEmpty(partnerId))
        {
            if (environment.IsProduction())
            {
                failures.Add(
                    $"{SiigoOptions.SectionName}:PartnerId is required in Production: Siigo rejects every call without a Partner-Id.");
            }
        }
        else if (IsUnresolvedPipelineToken(partnerId))
        {
            failures.Add(
                $"{SiigoOptions.SectionName}:PartnerId is an unresolved pipeline token: add SIIGO_PARTNER_ID to the Backend-prod group.");
        }
        else if (partnerId.Length is < PartnerIdMinLength or > PartnerIdMaxLength || !partnerId.All(char.IsAsciiLetterOrDigit))
        {
            failures.Add(
                $"{SiigoOptions.SectionName}:PartnerId must be {PartnerIdMinLength} to {PartnerIdMaxLength} alphanumeric characters, "
                + "without spaces.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>Lo que deja el pipeline cuando la variable no existe: <c>#{NOMBRE}#</c> literal (mismo criterio que
    /// <c>SupportPdfOptionsValidator</c>).</summary>
    private static bool IsUnresolvedPipelineToken(string value) =>
        value.Length > 4
        && value.StartsWith("#{", StringComparison.Ordinal)
        && value.EndsWith("}#", StringComparison.Ordinal);
}
