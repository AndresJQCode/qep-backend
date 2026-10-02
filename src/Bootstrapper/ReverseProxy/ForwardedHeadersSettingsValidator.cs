using Microsoft.Extensions.Options;

namespace Bootstrapper.ReverseProxy;

// Falla rápido al arrancar (ValidateOnStart), igual que SeedOptionsValidator y compañía: una red
// mal escrita que se ignorara dejaría al rate limiter con un bucket por nodo del ingress, sin error
// y sin log. Ojo: en .NET 10 IPNetwork acepta una red con bits de host y la enmascara, así que
// 10.50.0.21/24 no falla sino que confía en toda 10.50.0.0/24; el ancho lo decide el prefijo.
internal sealed class ForwardedHeadersSettingsValidator : IValidateOptions<ForwardedHeadersSettings>
{
    public ValidateOptionsResult Validate(string? name, ForwardedHeadersSettings options)
    {
        var failures = new List<string>();
        for (var i = 0; i < options.KnownNetworks.Count; i++)
        {
            var network = options.KnownNetworks[i];
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                failures.Add(
                    $"{ForwardedHeadersSettings.SectionName}:KnownNetworks:{i} '{network}' is not a valid "
                    + "CIDR network (e.g. 10.50.0.0/24).");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
