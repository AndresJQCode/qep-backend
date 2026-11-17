using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Purchases.Infrastructure.Pdf;

/// <summary>
/// Copia de <c>SupportPdfOptionsValidator</c>: falla rápido al arrancar (<c>ValidateOnStart</c>). En <c>Production</c> exige la clave:
/// sin ella <c>qcode-pdf</c> responde 401 y ninguna orden de compra se imprime, y es más barato un pod que no arranca que descubrirlo
/// cuando alguien pide el PDF. Un token del pipeline sin resolver (<c>#{…}#</c>, la variable falta en <c>Backend-prod</c>) cuenta como
/// ausente: no vacío, pasaría por configurado. En todo ambiente la URL va por HTTPS: la orden, con costos y proveedor, viaja en el
/// cuerpo del POST. Ningún mensaje lleva el valor: nombran la clave.
/// </summary>
internal sealed class PurchasesPdfOptionsValidator(IHostEnvironment environment) : IValidateOptions<PurchasesPdfOptions>
{
    public const int MaxTimeoutSeconds = 120;

    public ValidateOptionsResult Validate(string? name, PurchasesPdfOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{PurchasesPdfOptions.SectionName}:BaseUrl must be an absolute HTTPS URL: the purchase order travels in the request body.");
        }

        if (options.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"{PurchasesPdfOptions.SectionName}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");
        }

        if (environment.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                failures.Add(
                    $"{PurchasesPdfOptions.SectionName}:ApiKey is required in Production: without it qcode-pdf rejects every request and "
                    + "no purchase order can be printed.");
            }
            else if (IsUnresolvedPipelineToken(options.ApiKey))
            {
                failures.Add(
                    $"{PurchasesPdfOptions.SectionName}:ApiKey is an unresolved pipeline token in Production: add PURCHASES_PDF_API_KEY to "
                    + "the Backend-prod group.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>Lo que deja el pipeline cuando la variable no existe: <c>#{NOMBRE}#</c> literal (mismo criterio que
    /// <c>SeedOptions.IsUnreplacedPipelineToken</c>, que vive en Bootstrapper y este módulo no ve).</summary>
    private static bool IsUnresolvedPipelineToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 4
            && trimmed.StartsWith("#{", StringComparison.Ordinal)
            && trimmed.EndsWith("}#", StringComparison.Ordinal);
    }
}
