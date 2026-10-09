using FluentValidation;
using FluentValidation.Results;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Las reglas de entrada que ve el formulario (spec 2026-10-08, «Códigos de error»). Cada falla nace
/// acá con su nombre de propiedad exacto y en minúscula (<c>name</c>, <c>providerKey</c>,
/// <c>fields.fromNumber</c>, <c>secrets.apiToken</c>; P26): ninguna regla de los validadores deja que
/// FluentValidation derive el nombre, que saldría en PascalCase. Los mensajes van en español, tuteando,
/// y nunca llevan el valor que se mandó: terminan en ProblemDetails, en el log y en
/// <c>platform.request_failures</c>.
/// </summary>
internal static class ConnectionInputRules
{
    public const string NameMessage = "Escribe un nombre de 1 a 80 caracteres, en una sola línea.";
    public const string ProviderUnknownMessage = "Elige un proveedor del catálogo.";
    public const string RequiredMessage = "Completa este campo.";
    public const string UnknownFieldMessage = "Este campo no existe para este proveedor.";
    public const string UnknownKeysMessage = "Llegaron campos que este proveedor no tiene.";
    public const string CredentialsRejectedMessage = "El proveedor rechazó esta clave: revísala y vuelve a pegarla.";
    public const string ProviderRejectedFieldMessage = "El proveedor no aceptó este valor: revísalo.";
    public const string UnreadableSecretMessage = "La clave guardada ya no se puede leer: pégala de nuevo.";
    public const string ProviderUsesMetaSignupMessage = "Este proveedor se conecta desde el flujo de Meta.";
    public const string ReadOnlyFieldMessage = "Este campo lo llena el backend; no se puede editar.";

    private const string FieldsPrefix = "fields";
    private const string SecretsPrefix = "secrets";

    public static string TooLongMessage(int maxLength) => $"Usa máximo {maxLength} caracteres.";

    public static IEnumerable<ValidationFailure> CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > IntegrationConnection.NameMaxLength || trimmed.Any(char.IsControl))
        {
            yield return new ValidationFailure("name", NameMessage);
        }
    }

    /// <param name="requireSecrets">En el POST todo secreto requerido tiene que venir; en el PUT el
    /// ausente conserva el guardado (D5).</param>
    public static IEnumerable<ValidationFailure> CheckValues(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string?>? fields,
        IReadOnlyDictionary<string, string?>? secrets,
        bool requireSecrets) =>
        CheckGroup(provider, fields, secret: false, require: true)
            .Concat(CheckGroup(provider, secrets, secret: true, require: requireSecrets));

    /// <summary>Los valores no vacíos del grupo, recortados. Lo inválido ya lo rechazó el validador.</summary>
    public static Dictionary<string, string> Normalize(
        IntegrationProvider provider, IReadOnlyDictionary<string, string?>? values, bool secret)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null)
        {
            return normalized;
        }

        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            if (values.TryGetValue(definition.Key, out var raw) && raw?.Trim() is { Length: > 0 } value)
            {
                normalized[definition.Key] = value;
            }
        }

        return normalized;
    }

    public static void ThrowIfAny(IEnumerable<ValidationFailure> failures)
    {
        var list = failures.ToList();
        if (list.Count > 0)
        {
            throw new ValidationException(list);
        }
    }

    public static void AddTo<T>(ValidationContext<T> context, IEnumerable<ValidationFailure> failures)
    {
        foreach (var failure in failures)
        {
            context.AddFailure(failure);
        }
    }

    /// <summary><c>fields.&lt;key&gt;</c> o <c>secrets.&lt;key&gt;</c> según el catálogo; <c>fields</c>
    /// si la clave no es del proveedor.</summary>
    public static string PropertyFor(IntegrationProvider provider, string? fieldKey) =>
        provider.FindField(fieldKey ?? string.Empty) is { } definition
            ? $"{(definition.IsSecret ? SecretsPrefix : FieldsPrefix)}.{definition.Key}"
            : FieldsPrefix;

    private static IEnumerable<ValidationFailure> CheckGroup(
        IntegrationProvider provider, IReadOnlyDictionary<string, string?>? values, bool secret, bool require)
    {
        var prefix = secret ? SecretsPrefix : FieldsPrefix;

        // Un diccionario null (no un valor null) es un diccionario vacío: el requerido sale como
        // "Completa este campo." y nunca como un 500.
        values ??= new Dictionary<string, string?>();

        // Review Focus 2: un secreto dentro de fields es "no existe", no un valor que se guarda en claro.
        // Una clave con forma rara se reporta bajo el grupo, sin repetirla en el mapa errors.
        var weirdKey = false;
        foreach (var key in values.Keys)
        {
            if (provider.FindField(key) is { } known && known.IsSecret == secret)
            {
                // Spec 2026-10-09 §6.1: un campo interno, o cualquier campo de un proveedor con Embedded
                // Signup, lo escribe sólo el backend. Un valor vacío cuenta como "no vino".
                if ((known.Internal || provider.Onboarding == ProviderOnboarding.MetaEmbeddedSignup)
                    && !string.IsNullOrWhiteSpace(values[key]))
                {
                    yield return new ValidationFailure($"{prefix}.{key}", ReadOnlyFieldMessage);
                }

                continue;
            }

            if (FieldDefinition.IsValidKey(key))
            {
                yield return new ValidationFailure($"{prefix}.{key}", UnknownFieldMessage);
            }
            else
            {
                weirdKey = true;
            }
        }

        if (weirdKey)
        {
            yield return new ValidationFailure(prefix, UnknownKeysMessage);
        }

        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            var property = $"{prefix}.{definition.Key}";
            var value = values.TryGetValue(definition.Key, out var raw) ? raw?.Trim() : null;
            if (string.IsNullOrEmpty(value))
            {
                if (require && definition.Required)
                {
                    yield return new ValidationFailure(property, RequiredMessage);
                }

                continue;
            }

            if (definition.IsTooLong(value))
            {
                yield return new ValidationFailure(property, TooLongMessage(definition.MaxLength));
            }
            else if (!definition.HasValidShape(value))
            {
                yield return new ValidationFailure(property, definition.InvalidMessage);
            }
        }
    }
}
