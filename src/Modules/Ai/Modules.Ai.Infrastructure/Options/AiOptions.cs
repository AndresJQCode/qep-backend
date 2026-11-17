using Modules.Ai.Application;
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

    /// <summary>Spec 2026-10-29 §9.1, O-6: los niveles de <c>reasoning.effort</c> de la Responses API. Cuáles acepta cada modelo lo dice
    /// OpenAI; uno no soportado responde 400 (<c>InvalidRequest</c>).</summary>
    private static readonly string[] OpenAiEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>spec 2026-10-29 §4.2: <c>ai.agent_versions.model</c> es <c>varchar(64)</c>.</summary>
    private const int ModelIdMaxLength = 64;

    /// <summary>El proveedor que toma un borrador nuevo.</summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>Por clave de proveedor (<c>anthropic</c>, <c>openai</c>). Ordinal: «Anthropic» en la configuración es un error, no un
    /// alias.</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Spec 2026-11-17 (copiloto) D-C11: <c>Ai:Copilot</c>.</summary>
    public AiCopilotOptions Copilot { get; set; } = new();

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

        Copilot.Validate(failures);

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

            // C-7: con Claude el esfuerzo va siempre explícito (Opus 5.5 rechaza apagar el pensamiento; el esfuerzo es la palanca). O-6:
            // con OpenAI es opcional (sin él, el default del modelo), pero si viene tiene que ser un nivel de la API.
            if (key == AiProviders.Anthropic.Value && !AnthropicEfforts.Contains(model.Effort, StringComparer.Ordinal))
            {
                failures.Add($"{at}:Effort must be one of: {string.Join(", ", AnthropicEfforts)}.");
            }
            else if (key == AiProviders.OpenAi.Value && model.Effort is not null && !OpenAiEfforts.Contains(model.Effort, StringComparer.Ordinal))
            {
                failures.Add($"{at}:Effort must be empty or one of: {string.Join(", ", OpenAiEfforts)}.");
            }
        }
    }
}

/// <summary>Spec 2026-11-17 (copiloto) D-C11: los topes por miembro. Defaults del spec; los números van en configuración, no en código.</summary>
public sealed class AiCopilotOptions
{
    /// <summary>Preguntas por miembro en una hora móvil (429 <c>ai.copilot.rate_limited</c>).</summary>
    public int MessagesPerHour { get; set; } = 30;

    /// <summary>Preguntas por miembro en 24 horas móviles.</summary>
    public int MessagesPerDay { get; set; } = 200;

    /// <summary>D-C11: el tope de salida de cada llamada del copiloto.</summary>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>D-C11: llamadas al modelo por trabajo.</summary>
    public int MaxModelCalls { get; set; } = 8;

    /// <summary>D-C11: llamadas a herramientas por trabajo.</summary>
    public int MaxToolCalls { get; set; } = 12;

    /// <summary>D-C11: el tope de todo un trabajo.</summary>
    public int JobTimeoutSeconds { get; set; } = 120;

    /// <summary>D-C9: los últimos mensajes del hilo que viajan al modelo.</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>D-C9: el tope del historial en tokens, estimado por caracteres. <c>null</c>: sólo rige <see cref="MaxHistoryMessages"/>.</summary>
    public int? MaxContextTokens { get; set; }

    public CopilotLimits ToLimits() => new(MessagesPerHour, MessagesPerDay);

    public CopilotRunLimits ToRunLimits() =>
        new(new AiRunLimits(
            MaxOutputTokens, TimeSpan.FromSeconds(JobTimeoutSeconds), MaxModelCalls, MaxToolCalls, MaxHistoryMessages, MaxContextTokens));

    internal void Validate(List<string> failures)
    {
        const string path = "Ai:Copilot";
        if (MessagesPerHour < 1)
        {
            failures.Add($"{path}:MessagesPerHour must be at least 1.");
        }

        if (MessagesPerDay < MessagesPerHour)
        {
            failures.Add($"{path}:MessagesPerDay must be at least {path}:MessagesPerHour.");
        }

        // Un cero dejaría al copiloto sin poder contestar nunca: frena el arranque.
        foreach (var (value, key) in new[]
        {
            (MaxOutputTokens, nameof(MaxOutputTokens)), (MaxModelCalls, nameof(MaxModelCalls)), (MaxToolCalls, nameof(MaxToolCalls)),
            (JobTimeoutSeconds, nameof(JobTimeoutSeconds)), (MaxHistoryMessages, nameof(MaxHistoryMessages)), (MaxContextTokens ?? 1, nameof(MaxContextTokens)),
        })
        {
            if (value < 1)
            {
                failures.Add($"{path}:{key} must be greater than zero.");
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
