using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bootstrapper.Valkey;

/// <summary>
/// Falla rápido al arrancar (<c>ValidateOnStart</c>). En <c>Production</c> exige <c>ConnectionStrings:Valkey</c> aunque
/// todas las áreas estén apagadas (spec 2026-11-13, D-1): así prender un área es cambiar un flag, no descubrir en ese
/// momento que el Secret no tenía la cadena. Vacía o con el token del pipeline sin resolver (<c>#{…}#</c>) cuenta como
/// ausente, igual que en <c>ShopifyAppOptionsValidator</c>. Los mensajes nombran la clave y nunca el valor: la cadena
/// lleva la contraseña del usuario ACL y el host.
/// </summary>
internal sealed class ValkeyOptionsValidator(IHostEnvironment environment, IConfiguration configuration)
    : IValidateOptions<ValkeyOptions>
{
    private const string ConnectionStringKey = "ConnectionStrings:" + ValkeyConnection.ConnectionStringName;

    public ValidateOptionsResult Validate(string? name, ValkeyOptions options)
    {
        var failures = new List<string>();
        if (options.OperationTimeoutMs <= 0)
        {
            failures.Add($"{ValkeyOptions.SectionName}:OperationTimeoutMs must be greater than zero.");
        }

        if (environment.IsProduction() && ValkeyConnection.ReadConnectionString(configuration) is null)
        {
            failures.Add(
                $"{ConnectionStringKey} is required in Production, even with every Valkey area off; "
                + "an unresolved pipeline token counts as missing.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
