namespace Modules.Integrations.Infrastructure.SecretProtection;

/// <summary>
/// La sección <c>Integrations:SecretProtection</c> (spec 2026-10-08, «Secreto en reposo»; era
/// <c>Quotations:SecretProtection</c> en 6612298). <c>ActiveKeyId</c> no es secreto (va en el
/// ConfigMap); cada <c>Keys:&lt;id&gt;</c> sí (va en el Secret, desde una variable secreta del
/// pipeline). <b>Vacío = ausente</b> en todas partes: el harness de integración fija
/// <c>Keys:k1 = ""</c> para tapar los user-secrets del developer.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Integrations:SecretProtection";

    public string? ActiveKeyId { get; init; }

    public Dictionary<string, string?> Keys { get; init; } = new(StringComparer.Ordinal);

    internal string? EffectiveActiveKeyId =>
        string.IsNullOrWhiteSpace(ActiveKeyId) ? null : ActiveKeyId.Trim();

    internal string? KeyValue(string keyId) =>
        Keys.TryGetValue(keyId, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    internal static string KeyPath(string keyId) => $"{SectionName}:Keys:{keyId}";
}
