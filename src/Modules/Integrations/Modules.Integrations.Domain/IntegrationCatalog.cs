using System.Text.RegularExpressions;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public enum IntegrationCategory
{
    Messaging,
    Shipping,
    Ai,
}

public enum FieldKind
{
    Text,
    Secret,
    Phone,
    Url,
}

/// <summary>
/// Un campo que el proveedor pide para conectarse (spec 2026-10-08, «Catálogo»). Su clave es la del
/// JSON y la de <c>fields.&lt;key&gt;</c> en el mapa <c>errors</c>. <see cref="Label"/> e
/// <see cref="InvalidMessage"/> los ve una persona: en español, tuteando, y nunca con el valor.
/// </summary>
public sealed class FieldDefinition
{
    public const int KeyMaxLength = 40;

    private static readonly Regex KeyShape = new(
        "^[a-zA-Z][a-zA-Z0-9]{1,39}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FieldDefinition(
        string key,
        string label,
        FieldKind kind,
        bool required,
        int maxLength,
        string? pattern,
        string invalidMessage)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"'{key}' is not a valid field key: it must match {KeyShape}.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);

        Key = key;
        Label = label;
        Kind = kind;
        Required = required;
        MaxLength = maxLength;
        Pattern = pattern is null ? null : new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        InvalidMessage = invalidMessage;
    }

    public string Key { get; }

    public string Label { get; }

    public FieldKind Kind { get; }

    public bool Required { get; }

    public int MaxLength { get; }

    public Regex? Pattern { get; }

    public string InvalidMessage { get; }

    public bool IsSecret => Kind == FieldKind.Secret;

    public static bool IsValidKey(string? key) => key is not null && KeyShape.IsMatch(key);

    public bool IsTooLong(string value) => value.Length > MaxLength;

    /// <summary>
    /// Sin caracteres de control —un <c>\0</c> en <c>text</c> o en <c>jsonb</c> lo rechaza PostgreSQL
    /// con un 500 (Review Focus 1)— y con el patrón del catálogo, si lo hay.
    /// </summary>
    public bool HasValidShape(string value) =>
        !value.Any(char.IsControl) && (Pattern is null || Pattern.IsMatch(value));
}

/// <summary>
/// Un proveedor del catálogo (spec 2026-10-08, «Catálogo»). Un proveedor nuevo es otra instancia en
/// <see cref="IntegrationProviders"/>, su probador y sus pruebas, más la migración que cambia el
/// <c>CHECK</c> de <c>provider_key</c> (D10).
/// </summary>
public sealed class IntegrationProvider
{
    public const int KeyMaxLength = 32;

    private static readonly Regex KeyShape = new(
        "^[a-z0-9-]{2,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IntegrationProvider(
        string key,
        string displayName,
        IntegrationCategory category,
        IReadOnlyList<TenantModuleKey> consumingModules,
        IReadOnlyList<FieldDefinition> fields,
        int maxConnections)
    {
        if (!KeyShape.IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a valid provider key: it must match {KeyShape}.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        if (fields.Count == 0)
        {
            throw new ArgumentException("A provider needs at least one field.", nameof(fields));
        }

        if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("Field keys must be unique within a provider.", nameof(fields));
        }

        Key = key;
        DisplayName = displayName;
        Category = category;
        ConsumingModules = consumingModules.ToArray();
        Fields = fields.ToArray();
        PublicFields = Fields.Where(field => !field.IsSecret).ToArray();
        SecretFields = Fields.Where(field => field.IsSecret).ToArray();
        MaxConnections = maxConnections;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public IntegrationCategory Category { get; }

    /// <summary>Los módulos que usan este proveedor; vacío = nunca visible.</summary>
    public IReadOnlyList<TenantModuleKey> ConsumingModules { get; }

    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>Los que van a <c>fields</c> (jsonb).</summary>
    public IReadOnlyList<FieldDefinition> PublicFields { get; }

    /// <summary>Los que van a <c>connection_secrets</c>, cifrados.</summary>
    public IReadOnlyList<FieldDefinition> SecretFields { get; }

    /// <summary>Tope por tenant y proveedor (D1).</summary>
    public int MaxConnections { get; }

    public FieldDefinition? FindField(string key) =>
        Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Spec 2026-10-08, «Visibilidad»: alguno de sus módulos consumidores está efectivo. Con el stub de
    /// desarrollo (<paramref name="modules"/> en <c>null</c>), todo; sin consumidores, nunca.
    /// </summary>
    public bool IsVisibleFor(TenantModuleSet? modules) =>
        ConsumingModules.Count > 0 && (modules is null || ConsumingModules.Any(modules.IsEnabled));
}
