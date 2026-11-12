using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Options;

/// <summary>
/// Plan 2026-11-12, C-3: la lista curada de modelos, por proveedor. Las reglas viven en <see cref="Validate"/> para probarlas sin host;
/// <see cref="AiOptionsValidator"/> las corre al arrancar (<c>ValidateOnStart</c>), así que una lista rota no deja levantar el pod.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>C-7: los niveles de esfuerzo que acepta la API de Anthropic. Se escriben en minúscula, como viajan.</summary>
    private static readonly string[] AnthropicEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>spec 2026-10-29 §4.2: <c>ai.agent_versions.model</c> es <c>varchar(64)</c>.</summary>
    private const int ModelIdMaxLength = 64;

    /// <summary>El proveedor que toma un borrador nuevo.</summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>Por clave de proveedor (<c>anthropic</c>, <c>openai</c>). Ordinal: «Anthropic» en la configuración es un error, no un
    /// alias.</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Los mensajes nombran la clave de configuración que falla; ninguno lleva un valor secreto (aquí no hay ninguno).</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();

        if (!AiProvider.TryParse(DefaultProvider, out _))
        {
            failures.Add($"{SectionName}:DefaultProvider must be one of: {string.Join(", ", AiProviders.All)}.");
        }

        foreach (var key in Providers.Keys.Where(key => !AiProvider.TryParse(key, out _)))
        {
            failures.Add($"{SectionName}:Providers:{key} is not a known AI provider ({string.Join(", ", AiProviders.All)}).");
        }

        // C-16: la pantalla ofrece todos los proveedores siempre; uno sin sección no tendría qué mostrar.
        foreach (var provider in AiProviders.All.Where(provider => !Providers.ContainsKey(provider.Value)))
        {
            failures.Add($"{SectionName}:Providers:{provider} is required: every AI provider needs its curated model list.");
        }

        foreach (var (key, provider) in Providers)
        {
            ValidateProvider(key, provider, failures);
        }

        if (AiProvider.TryParse(DefaultProvider, out _)
            && Providers.TryGetValue(DefaultProvider, out var defaultProvider)
            && defaultProvider.Models.Count(model => model.IsDefault) == 1
            && !defaultProvider.Models.Single(model => model.IsDefault).Available)
        {
            failures.Add($"{SectionName}:Providers:{DefaultProvider}: the default model of the default provider must be available, or a new agent could not be published.");
        }

        return failures;
    }

    private static void ValidateProvider(string key, AiProviderOptions provider, List<string> failures)
    {
        var path = $"{SectionName}:Providers:{key}:Models";
        if (provider.Models.Count == 0)
        {
            failures.Add($"{path} needs at least one model.");
            return;
        }

        if (provider.Models.Count(model => model.IsDefault) != 1)
        {
            failures.Add($"{path} needs exactly one model with IsDefault = true.");
        }

        foreach (var id in provider.Models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key))
        {
            failures.Add($"{path}: the model id '{id}' is repeated.");
        }

        for (var index = 0; index < provider.Models.Count; index++)
        {
            var model = provider.Models[index];
            var at = $"{path}:{index}";
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                failures.Add($"{at}:Id is required.");
            }
            else if (model.Id.Length > ModelIdMaxLength)
            {
                failures.Add($"{at}:Id must be at most {ModelIdMaxLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(model.Label))
            {
                failures.Add($"{at}:Label is required.");
            }

            // C-7: con Claude el esfuerzo va siempre explícito (Opus 5.5 rechaza apagar el pensamiento; el esfuerzo es la palanca). El
            // de OpenAI lo fija P-1, en la Tarea 6.
            if (key == AiProviders.Anthropic.Value && !AnthropicEfforts.Contains(model.Effort, StringComparer.Ordinal))
            {
                failures.Add($"{at}:Effort must be one of: {string.Join(", ", AnthropicEfforts)}.");
            }
        }
    }
}

public sealed class AiProviderOptions
{
    public List<AiModelOptions> Models { get; set; } = [];
}

public sealed class AiModelOptions
{
    /// <summary>El id exacto que se manda al proveedor (<c>claude-opus-5-5</c>), sin sufijo de fecha.</summary>
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    /// <summary><c>false</c>: se lista pero no se publica con él (retirado, o su proveedor todavía no tiene adaptador).</summary>
    public bool Available { get; set; } = true;

    public string? Effort { get; set; }
}
