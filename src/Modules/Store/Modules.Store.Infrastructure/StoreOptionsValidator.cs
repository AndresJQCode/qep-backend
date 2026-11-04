using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Store.Application;

namespace Modules.Store.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart). Las reglas viven en StoreOptions.Validate para probarlas sin host. Fuera de Development y
// Testing los secretos son obligatorios: producción sin ellos no arranca, en vez de aceptar llamadas sin firma verificable.
internal sealed class StoreOptionsValidator(IHostEnvironment environment) : IValidateOptions<StoreOptions>
{
    public ValidateOptionsResult Validate(string? name, StoreOptions options)
    {
        var requireSecrets = !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
        var failures = options.Validate(requireSecrets);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
