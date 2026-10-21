using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Quotations.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart), mismo criterio que StorageOptionsValidator.
internal sealed class QuotationsOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<QuotationsOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotationsOptions options)
    {
        var failures = new List<string>();

        if (options.ExpirationSweepMinutes <= 0)
        {
            failures.Add("Quotations:ExpirationSweepMinutes must be greater than zero.");
        }

        // WhatsApp no se exige: la cuenta de Zenvia es de cada tenant y vive en su conexión (plan
        // 2026-10-21). Un tenant sin ella recibe `quotation.whatsapp.no_channel`, que es ruidoso.
        if (environment.IsProduction())
        {
            // Sin la key `qcode-pdf` responde 401 y el envío falla con `quotation.pdf.render_failed`,
            // que es ruidoso. Se exige igual al arrancar porque descubrirlo cuando una asesora
            // aprieta Enviar es mucho más caro que un proceso que no levanta.
            if (string.IsNullOrWhiteSpace(options.Pdf.ApiKey))
            {
                failures.Add(
                    "Quotations:Pdf:ApiKey is required in Production: without it qcode-pdf "
                    + "rejects every request and no quotation can be sent.");
            }
        }

        // En cualquier ambiente: la plantilla y los datos de la cotización -- precios, cliente,
        // totales -- viajan en el cuerpo del POST, así que sin TLS quedan expuestos en tránsito.
        if (!Uri.TryCreate(options.Pdf.BaseUrl, UriKind.Absolute, out var pdfBaseUrl) ||
            pdfBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                "Quotations:Pdf:BaseUrl must be an absolute HTTPS URL: the quotation data "
                + "travels in the request body.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
