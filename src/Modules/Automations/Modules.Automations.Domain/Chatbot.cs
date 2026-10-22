namespace Modules.Automations.Domain;

/// <summary>
/// Spec 2026-10-21 §4.2 y §4.3: el chatbot del tenant, con su borrador editable, sus versiones publicadas (inmutables) y las
/// conexiones que atiende. Acá sólo las propiedades que EF mapea; el comportamiento (crear, publicar, pausar) llega con la tarea
/// 12 del plan.
/// </summary>
public sealed class Chatbot
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 300;
    public const int RestartKeywordMaxLength = 40;
    public const string DefaultRestartKeyword = "reiniciar";
    public const int DefaultMaxAttempts = 3;

    private readonly List<ChatbotVersion> _versions = [];
    private readonly List<ChatbotConnection> _connections = [];

    private Chatbot()
    {
        Name = string.Empty;
        Description = string.Empty;
        Keywords = [];
        RestartKeyword = DefaultRestartKeyword;
        DraftFlowJson = "{}";
        DraftLayoutJson = "{}";
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public ChatbotStatus Status { get; private set; }

    public ChatbotTrigger Trigger { get; private set; }

    /// <summary>Normalizadas (sin tildes ni mayúsculas) por el agregado; jsonb en la base.</summary>
    public IReadOnlyList<string> Keywords { get; private set; }

    public string RestartKeyword { get; private set; }

    public int MaxAttempts { get; private set; }

    /// <summary>El flujo del Anexo A tal como lo guardó el editor; se valida al guardar y al publicar.</summary>
    public string DraftFlowJson { get; private set; }

    /// <summary>Posiciones de los bloques en el lienzo: el motor no lo lee.</summary>
    public string DraftLayoutJson { get; private set; }

    public DateTimeOffset DraftUpdatedAt { get; private set; }

    public Guid DraftUpdatedBy { get; private set; }

    /// <summary>Null mientras es borrador (<c>CK_chatbots_published</c>).</summary>
    public Guid? PublishedVersionId { get; private set; }

    public IReadOnlyCollection<ChatbotVersion> Versions => _versions;

    public IReadOnlyCollection<ChatbotConnection> Connections => _connections;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Concurrencia optimista con <c>If-Match</c>, al estilo Messaging (428/412).</summary>
    public long Version { get; private set; }
}
