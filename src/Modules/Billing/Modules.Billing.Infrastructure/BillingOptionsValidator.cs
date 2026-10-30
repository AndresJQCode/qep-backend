using Microsoft.Extensions.Options;
using Modules.Billing.Application;

namespace Modules.Billing.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart), mismo criterio que QuotationsOptionsValidator. Las reglas viven en
// BillingOptions.Validate para probarlas sin host.
internal sealed class BillingOptionsValidator : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        var failures = options.Validate();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
