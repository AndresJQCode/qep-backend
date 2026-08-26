using Microsoft.Extensions.Options;

namespace Modules.Quotations.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart), mismo criterio que StorageOptionsValidator.
internal sealed class QuotationsOptionsValidator : IValidateOptions<QuotationsOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotationsOptions options) =>
        options.ExpirationSweepMinutes <= 0
            ? ValidateOptionsResult.Fail("Quotations:ExpirationSweepMinutes must be greater than zero.")
            : ValidateOptionsResult.Success;
}
