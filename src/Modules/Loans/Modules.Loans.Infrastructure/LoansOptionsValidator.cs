using Microsoft.Extensions.Options;
using Modules.Loans.Application;

namespace Modules.Loans.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart). Las reglas viven en LoansOptions.Validate para probarlas sin host.
internal sealed class LoansOptionsValidator : IValidateOptions<LoansOptions>
{
    public ValidateOptionsResult Validate(string? name, LoansOptions options)
    {
        var failures = options.Validate();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
