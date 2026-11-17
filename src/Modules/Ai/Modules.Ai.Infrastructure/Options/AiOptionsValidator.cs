using Microsoft.Extensions.Options;

namespace Modules.Ai.Infrastructure.Options;

// Falla rápido al arrancar (ValidateOnStart). Las reglas viven en AiOptions.Validate para probarlas sin host.
internal sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var failures = options.Validate();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
