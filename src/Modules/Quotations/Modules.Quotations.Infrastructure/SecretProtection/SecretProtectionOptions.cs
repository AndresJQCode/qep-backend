namespace Modules.Quotations.Infrastructure.SecretProtection;

/// <summary>
/// La sección <c>Quotations:SecretProtection</c> (spec 2026-10-07). Propia y registrada aparte de
/// <see cref="QuotationsOptions"/> para que sus reglas no se mezclen con las de WhatsApp y PDF.
///
/// <c>ActiveKeyId</c> no es secreto (va en el ConfigMap); cada <c>Keys:&lt;id&gt;</c> sí (va en el
/// Secret, desde una variable secreta del pipeline). <b>Vacío = ausente</b> en todas partes: el
/// harness de integración fija <c>Keys:k1 = ""</c> para tapar los user-secrets del developer.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Quotations:SecretProtection";

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
