using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

// Falla rápido al arrancar (ValidateOnStart), igual que QuotationsOptionsValidator: con la opción
// encendida y sin bucket público, ningún comprobante tendría copia pública y el Excel de pedidos
// saldría sin un solo enlace, sin error y sin log (spec 2026-09-15, P2). Vive acá y no en
// Quotations.Infrastructure porque ése no referencia Storage: el composition root es el único
// proyecto que ve las dos opciones. Vale en cualquier ambiente, no sólo en producción.
internal sealed class PaymentProofsOptionsValidator(IOptions<StorageOptions> storageOptions)
    : IValidateOptions<QuotationsOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotationsOptions options)
    {
        if (!options.PaymentProofs.PublicLinks)
        {
            return ValidateOptionsResult.Success;
        }

        var r2 = storageOptions.Value.R2;
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(r2.PublicBucket))
        {
            failures.Add(Required("Storage:R2:PublicBucket"));
        }

        if (string.IsNullOrWhiteSpace(r2.PublicBaseUrl))
        {
            failures.Add(Required("Storage:R2:PublicBaseUrl"));
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static string Required(string key) =>
        $"{key} is required when Quotations:PaymentProofs:PublicLinks is true: without it no payment "
        + "proof gets a public copy and the orders Excel has no links.";
}
