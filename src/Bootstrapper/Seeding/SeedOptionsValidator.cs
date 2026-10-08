using Microsoft.Extensions.Options;
using Modules.Identity.Domain;

namespace Bootstrapper.Seeding;

// Falla rápido al arrancar (ValidateOnStart), igual que AuditOptionsValidator y compañía:
// una semilla prendida sin email sembraría un tenant al que nadie puede entrar, y eso se
// descubriría recién al intentar usarlo.
internal sealed class SeedOptionsValidator : IValidateOptions<SeedOptions>
{
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        if (options.ExportLoad.Quotations < 0)
        {
            return ValidateOptionsResult.Fail("Seed:ExportLoad:Quotations cannot be negative.");
        }

        // Las dos semillas le conceden admin a OwnerEmail: cualquiera de las dos prendida lo exige.
        var requiredBy = options.Enabled
            ? "Seed:Enabled is true"
            : options.ExportLoad.Quotations > 0
                ? "Seed:ExportLoad:Quotations is greater than 0"
                : null;
        if (requiredBy is null)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.OwnerEmail))
        {
            return ValidateOptionsResult.Fail($"Seed:OwnerEmail is required when {requiredBy}.");
        }

        // Se normaliza con la misma regla del dominio de Identity, no con una propia: si el
        // email no pasa acá, tampoco va a pasar cuando el seeder cree el usuario, y el
        // arranque es mejor lugar para enterarse que el medio de la siembra.
        try
        {
            User.NormalizeEmail(options.OwnerEmail);
        }
        catch (IdentityDomainException)
        {
            return ValidateOptionsResult.Fail(
                $"Seed:OwnerEmail '{options.OwnerEmail}' is not a valid email address.");
        }

        return options.Enabled
            ? ValidateOperatorOwnerEmail(options.OperatorOwnerEmail)
            : ValidateOptionsResult.Success;
    }

    // Sólo la lee la semilla de arranque, así que sólo se valida con Seed:Enabled. Ausente es
    // válido —la semilla advierte y no crea el tenant operador—, y un token del pipeline sin
    // reemplazar vale lo mismo que ausente; presente e inválido tumba el arranque por la misma
    // razón que Seed:OwnerEmail.
    private static ValidateOptionsResult ValidateOperatorOwnerEmail(string? operatorOwnerEmail)
    {
        if (string.IsNullOrWhiteSpace(operatorOwnerEmail)
            || SeedOptions.IsUnreplacedPipelineToken(operatorOwnerEmail))
        {
            return ValidateOptionsResult.Success;
        }

        try
        {
            User.NormalizeEmail(operatorOwnerEmail);
        }
        catch (IdentityDomainException)
        {
            return ValidateOptionsResult.Fail(
                $"Seed:OperatorOwnerEmail '{operatorOwnerEmail}' is not a valid email address.");
        }

        return ValidateOptionsResult.Success;
    }
}
