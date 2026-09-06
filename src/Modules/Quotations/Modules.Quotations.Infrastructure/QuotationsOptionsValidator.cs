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

        // Sin las tres claves, AddWhatsAppSender cae a LogWhatsAppSender, que registra el
        // mensaje y no lo manda. Fuera de producción ese no-op es el que deja "Enviar"
        // funcionando sin aprovisionar Zenvia (todas las pruebas de integración incluidas).
        // En producción es una trampa silenciosa: la API responde 200, la cotización pasa a
        // Sent y el cliente nunca recibe nada — sin excepción, sin log de error y sin forma de
        // distinguirlo de un envío real. Se prefiere que el arranque falle a que el negocio
        // pierda ventas sin enterarse.
        if (environment.IsProduction())
        {
            failures.AddRange(MissingWhatsAppKeys(options.WhatsApp));
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static IEnumerable<string> MissingWhatsAppKeys(WhatsAppOptions whatsApp)
    {
        if (string.IsNullOrWhiteSpace(whatsApp.ApiToken))
        {
            yield return Required(nameof(WhatsAppOptions.ApiToken));
        }

        if (string.IsNullOrWhiteSpace(whatsApp.FromNumber))
        {
            yield return Required(nameof(WhatsAppOptions.FromNumber));
        }

        if (string.IsNullOrWhiteSpace(whatsApp.TemplateId))
        {
            yield return Required(nameof(WhatsAppOptions.TemplateId));
        }
    }

    private static string Required(string key) =>
        $"Quotations:WhatsApp:{key} is required in Production: without it the quotation is "
        + "marked as sent but no WhatsApp message leaves the system.";
}
