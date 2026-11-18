using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Support.Infrastructure.Pdf;

/// <summary>
/// Fase 4b (H12): falla rápido al arrancar (<c>ValidateOnStart</c>), como <c>QuotationsOptionsValidator</c>. En <c>Production</c> exige la
/// clave: sin ella <c>qcode-pdf</c> responde 401 y ninguna respuesta formal sale, y es más barato un pod que no arranca que descubrirlo
/// cuando un asesor aprieta Enviar. Un token del pipeline sin resolver (<c>#{…}#</c>, la variable falta en <c>Backend-prod</c>) cuenta
/// como ausente: no vacío, pasaría por configurado. En todo ambiente la URL va por HTTPS: la carta, con los datos del solicitante, viaja
/// en el cuerpo del POST. Ningún mensaje lleva el valor: nombran la clave.
/// </summary>
internal sealed class SupportPdfOptionsValidator(IHostEnvironment environment) : IValidateOptions<SupportPdfOptions>
{
    public const int MaxTimeoutSeconds = 120;

    public ValidateOptionsResult Validate(string? name, SupportPdfOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{SupportPdfOptions.SectionName}:BaseUrl must be an absolute HTTPS URL: the formal reply travels in the request body.");
        }

        if (options.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"{SupportPdfOptions.SectionName}:TimeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");
        }

        if (environment.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                failures.Add(
                    $"{SupportPdfOptions.SectionName}:ApiKey is required in Production: without it qcode-pdf rejects every request and "
                    + "no formal reply can be sent.");
            }
            else if (IsUnresolvedPipelineToken(options.ApiKey))
            {
                failures.Add(
                    $"{SupportPdfOptions.SectionName}:ApiKey is an unresolved pipeline token in Production: add QUOTATIONS_PDF_API_KEY (the qcode-pdf "
                    + "key shared by the whole QEP backend) to the Backend-prod group.");
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
