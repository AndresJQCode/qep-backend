using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Store.Application;

namespace Modules.Store.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart). Las reglas viven en StoreOptions.Validate para probarlas sin host. Los secretos son
// obligatorios sólo en Production, como en todos los validadores del repositorio; fuera de ahí, un secreto ausente deja la tienda
// cerrada en ejecución (401 / captcha 503), nunca abierta. Ojo: como el arranque es de toda la API, en Production un secreto faltante
// la detiene entera, no sólo la tienda. El anti-bot apagado, en cambio, se prohíbe en todo ambiente que no sea Development o Testing:
// el verificador que lo reemplaza acepta cualquier token, y Staging no puede arrancar con él.
internal sealed class StoreOptionsValidator(IHostEnvironment environment) : IValidateOptions<StoreOptions>
{
    public ValidateOptionsResult Validate(string? name, StoreOptions options)
    {
        var failures = options.Validate(
            requireSecrets: environment.IsProduction(),
            turnstileMayBeDisabled: environment.IsDevelopment() || environment.IsEnvironment("Testing"));
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
