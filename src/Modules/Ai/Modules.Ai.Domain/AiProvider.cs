namespace Modules.Ai.Domain;

/// <summary>
/// El proveedor del modelo de un agente (plan 2026-11-12, C-16). Sus únicas instancias son las de <see cref="AiProviders"/>: el
/// constructor es privado, así que un proveedor inventado no compila. El texto es el mismo que la clave del proveedor en
/// Integrations y la sección de <c>Ai:Providers</c>, y es el que se guarda en las columnas <c>provider</c> de <c>ai.*</c>.
/// </summary>
public sealed record AiProvider
{
    private AiProvider(string value) => Value = value;

    public string Value { get; }

    internal static AiProvider Define(string value) => new(value);

    /// <summary>No ignora mayúsculas, igual que los <c>CHECK</c>: «Anthropic» no es un proveedor.</summary>
    public static bool TryParse(string? value, out AiProvider? provider)
    {
        provider = AiProviders.All.FirstOrDefault(candidate => string.Equals(candidate.Value, value, StringComparison.Ordinal));
        return provider is not null;
    }

    /// <summary>Para la base y la configuración ya validada: un texto desconocido ahí es un dato corrupto, no algo que ignorar.</summary>
    public static AiProvider Parse(string value) =>
        TryParse(value, out var provider)
            ? provider!
            : throw new ArgumentException($"'{value}' is not a known AI provider.", nameof(value));

    public override string ToString() => Value;
}

/// <summary>
/// La lista cerrada de proveedores, en el orden en que la pantalla los ofrece. Agregar uno es: esta lista, su proveedor en
/// Integrations, su probador de clave, su adaptador del modelo y su sección en <c>Ai:Providers</c> (plan 2026-11-12, Global
/// Constraints).
/// </summary>
public static class AiProviders
{
    public static readonly AiProvider Anthropic = AiProvider.Define("anthropic");

    public static readonly AiProvider OpenAi = AiProvider.Define("openai");

    public static readonly IReadOnlyList<AiProvider> All = [Anthropic, OpenAi];
}
