using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.SecretProtection;

/// <summary>
/// Falla rápido al arrancar (spec 2026-10-08, «Secreto en reposo»; viene de 6612298). En <c>Production</c>, todo:
/// activa presente y con valor, todo id con el patrón y toda llave declarada de 32 bytes —una llave
/// mal pegada en la variable del pipeline se descubre en el deploy, no cuando alguien guarda—.
/// Fuera de <c>Production</c>, sólo la activa si está declarada: las pruebas de integración corren
/// en <c>Development</c> con los user-secrets de quien las corre, y el harness no puede borrar una
/// <c>Keys:&lt;id&gt;</c> que no conoce.
///
/// Ningún mensaje lleva un valor: nombran la clave de configuración o el id.
/// </summary>
internal sealed partial class SecretProtectionOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<SecretProtectionOptions>
{
    private const int KeyLength = 32;

    [GeneratedRegex("^[a-z0-9]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    public ValidateOptionsResult Validate(string? name, SecretProtectionOptions options)
    {
        var failures = new List<string>();
        var active = options.EffectiveActiveKeyId;

        if (environment.IsProduction())
        {
            foreach (var keyId in options.Keys.Keys)
            {
                if (!KeyIdPattern().IsMatch(keyId))
                {
                    failures.Add(
                        $"{SecretProtectionOptions.KeyPath(keyId)} has an invalid id: key ids must match ^[a-z0-9]{{1,32}}$.");
                    continue;
                }

                if (options.KeyValue(keyId) is { } value && !IsValidKey(value))
                {
                    failures.Add(InvalidKey(keyId));
                }
            }

            if (active is null)
            {
                failures.Add(
                    $"{SecretProtectionOptions.SectionName}:ActiveKeyId is required in Production: "
                    + "without it no connection can be created or edited.");
            }
            else
            {
                CheckActivePresence(options, active, failures);
            }
        }
        else if (active is not null && CheckActivePresence(options, active, failures)
            && !IsValidKey(options.KeyValue(active)!))
        {
            failures.Add(InvalidKey(active));
        }

        // P16, en todo ambiente: el PeriodicTimer no acepta cero.
        if (options.RekeyIntervalMinutes is < 1 or > 1440)
        {
            failures.Add($"{SecretProtectionOptions.SectionName}:RekeyIntervalMinutes must be between 1 and 1440.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    // true si la activa tiene id válido y valor; el formato del valor lo mira quien llama.
    private static bool CheckActivePresence(
        SecretProtectionOptions options, string active, List<string> failures)
    {
        if (!KeyIdPattern().IsMatch(active))
        {
            failures.Add(
                $"{SecretProtectionOptions.SectionName}:ActiveKeyId must match ^[a-z0-9]{{1,32}}$.");
            return false;
        }

        if (options.KeyValue(active) is null)
        {
            failures.Add(
                $"{SecretProtectionOptions.KeyPath(active)} is required: it is the active key "
                + $"({SecretProtectionOptions.SectionName}:ActiveKeyId).");
            return false;
        }

        return true;
    }

    private static bool IsValidKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == KeyLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string InvalidKey(string keyId) =>
        $"{SecretProtectionOptions.KeyPath(keyId)} must be {KeyLength} bytes encoded in base64.";
}
