using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Ai.Domain;

/// <summary>
/// Spec 2026-10-29 §4.6: el documento <c>config</c> del agente, el mismo en el borrador (<c>ai.agents.draft_config</c>) y en cada
/// versión publicada (<c>ai.agent_versions.config</c>). Viaja tal cual entre el editor y el backend, en camelCase. Los valores
/// cerrados (<c>tone</c>, <c>handoff.destination</c>) van en minúscula y <c>trigger</c> con el nombre del enum, como los lista el spec.
/// Los topes de cada campo los valida el endpoint (FluentValidation, con el campo); lo que falta para publicar lo dice
/// <see cref="AgentConfigValidator"/>.
/// </summary>
/// <param name="Model">Plan 2026-11-12 C-16: el proveedor es del agente, junto con el modelo.</param>
public sealed record AgentConfig(
    string Instructions,
    string Tone,
    string? Greeting,
    IReadOnlyList<AgentFaq> Faq,
    bool UseQuickReplies,
    bool AnswerOnlyFromSources,
    AgentTools Tools,
    AgentHandoff Handoff,
    string Trigger,
    AgentModelRef Model)
{
    public const int InstructionsMaxLength = 10_000;
    public const int GreetingMaxLength = 1000;
    public const int MaxFaq = 50;
    public const int FaqIdMaxLength = 64;
    public const int FaqQuestionMaxLength = 300;
    public const int FaqAnswerMaxLength = 2000;

    public static readonly IReadOnlyList<string> Tones = ["professional", "friendly", "casual", "formal"];

    /// <summary>D-A9: con el entrante que abre o reabre la conversación, o sólo fuera de horario.</summary>
    public static readonly IReadOnlyList<string> Triggers = ["NewConversation", "OutsideHours"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Lo que trae un agente recién creado en blanco: sin instrucciones (publicar lo pide), tono profesional, sólo con las
    /// fuentes, sin herramientas, traspaso a la bandeja sin asignar y el modelo predeterminado que elija quien llama.</summary>
    public static AgentConfig Default(AgentModelRef model) =>
        new(
            Instructions: string.Empty,
            Tone: "professional",
            Greeting: null,
            Faq: [],
            UseQuickReplies: false,
            AnswerOnlyFromSources: true,
            Tools: new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: false),
            Handoff: new AgentHandoff(
                AgentHandoff.ToUnassigned, MemberId: null, Message: null, AgentHandoff.DefaultMaxRepliesWithoutHuman,
                AgentHandoff.DefaultMaxConsecutiveFailures),
            Trigger: "NewConversation",
            Model: model);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Para lo que ya guardó el agregado: un documento que no se puede leer es un dato corrupto, no algo que ignorar.</summary>
    public static AgentConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AgentConfig>(json, Json)
        ?? throw new InvalidOperationException("The stored agent config is empty.");
}

/// <param name="Id">Lo que el modelo cita como fuente; lo elige el editor y es único dentro del agente.</param>
public sealed record AgentFaq(string Id, string Question, string Answer);

/// <summary>Spec 2026-10-29 §8: qué herramientas puede usar el agente.</summary>
public sealed record AgentTools(bool Catalog, bool OrderStatus, bool QuotationDraft);

/// <summary>Spec 2026-10-29 §4.6 y D-A21: a dónde va la conversación cuando el agente la suelta, y los traspasos deterministas.
/// «Equipo» no existe en el código (P-9): sólo la bandeja sin asignar o un miembro.</summary>
public sealed record AgentHandoff(
    string Destination, Guid? MemberId, string? Message, int MaxRepliesWithoutHuman, int MaxConsecutiveFailures)
{
    public const string ToUnassigned = "unassigned";
    public const string ToMember = "member";
    public const int MessageMaxLength = 1000;
    public const int MinRepliesWithoutHuman = 1;
    public const int MaxRepliesWithoutHumanLimit = 50;
    public const int DefaultMaxRepliesWithoutHuman = 20;
    public const int MinConsecutiveFailures = 1;
    public const int MaxConsecutiveFailuresLimit = 5;
    public const int DefaultMaxConsecutiveFailures = 2;

    public static readonly IReadOnlyList<string> Destinations = [ToUnassigned, ToMember];
}

/// <summary>Plan 2026-11-12 C-16: <c>provider</c> es la clave de <see cref="AiProviders"/> e <c>id</c> el del modelo en la lista curada
/// de ese proveedor. Texto y no <see cref="AiProvider"/>: un borrador puede guardar un proveedor que ya no existe, y lo marca el
/// validador en vez de no poder leerse.</summary>
public sealed record AgentModelRef(string Provider, string Id)
{
    public const int ProviderMaxLength = 24;
    public const int IdMaxLength = 64;
}
