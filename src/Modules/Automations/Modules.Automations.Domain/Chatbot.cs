using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Domain;

/// <summary>
/// Spec 2026-10-21 §4.2 y §4.3: el chatbot del tenant, con su borrador editable, sus versiones publicadas (inmutables) y las
/// conexiones que atiende. Acá sólo las propiedades que EF mapea; D-C1, D-C5, D-C7, D-C12 y D-C13.
/// </summary>
public sealed class Chatbot
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 300;
    public const int RestartKeywordMaxLength = 40;
    public const string DefaultRestartKeyword = "reiniciar";
    public const int DefaultMaxAttempts = 3;
    public const int MaxKeywords = 10;
    public const int KeywordMaxLength = 40;
    public const int MinAttempts = 1;
    public const int MaxAttemptsLimit = 5;

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

    /// <summary>Nace en borrador con la versión 1 de concurrencia (el DEFAULT de la columna).</summary>
    public static Chatbot Create(
        Guid tenantId, string name, string description, ChatbotTrigger trigger, IReadOnlyList<string> keywords,
        string draftFlowJson, string draftLayoutJson, Guid by, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = NormalizeName(name),
            Description = NormalizeDescription(description),
            Status = ChatbotStatus.Draft,
            Trigger = trigger,
            Keywords = NormalizeKeywords(keywords),
            MaxAttempts = DefaultMaxAttempts,
            DraftFlowJson = draftFlowJson,
            DraftLayoutJson = draftLayoutJson,
            DraftUpdatedAt = now,
            DraftUpdatedBy = by,
            CreatedAt = now,
            CreatedBy = by,
            UpdatedAt = now,
            Version = 1,
        };

    public void Rename(string name, string description, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        Description = NormalizeDescription(description);
        Touch(now);
    }

    /// <summary>D-C7: palabra de reinicio y tope de intentos son del chatbot; <paramref name="maxAttempts"/> llega validado (1..5)
    /// por el endpoint, y el CHECK de la base es la última red.</summary>
    public void UpdateSettings(ChatbotTrigger trigger, IReadOnlyList<string> keywords, string restartKeyword, int maxAttempts, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, MinAttempts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, MaxAttemptsLimit);
        var normalizedKeywords = NormalizeKeywords(keywords);
        var restart = TextMatch.Normalize(restartKeyword.Trim());
        if (restart.Length > RestartKeywordMaxLength)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.ChatbotKeywordTooLong, $"La palabra de reinicio no puede pasar de {RestartKeywordMaxLength} caracteres.");
        }

        Trigger = trigger;
        Keywords = normalizedKeywords;
        RestartKeyword = restart.Length == 0 ? DefaultRestartKeyword : restart;
        MaxAttempts = maxAttempts;
        Touch(now);
    }

    /// <summary>Guarda el borrador tal cual lo mandó el editor: un flujo a medias es válido, la validación estricta es la de
    /// publicar.</summary>
    public void SaveDraft(string flowJson, string layoutJson, Guid by, DateTimeOffset now)
    {
        DraftFlowJson = flowJson;
        DraftLayoutJson = layoutJson;
        DraftUpdatedAt = now;
        DraftUpdatedBy = by;
        Touch(now);
    }

    /// <summary>D-C1: copia el borrador a una versión inmutable. D-C5: <c>Draft</c> pasa a <c>Active</c>; <c>Paused</c> se queda
    /// pausado. D-C13: publicar ocupa todas las conexiones del chatbot.</summary>
    public ChatbotVersion Publish(IReadOnlyList<FlowIssue> issues, Guid by, DateTimeOffset now)
    {
        if (issues.Any(issue => issue.Severity == FlowIssueSeverity.Error))
        {
            throw new AutomationsDomainException(AutomationsErrorCodes.ChatbotInvalid, "El flujo tiene errores: corrígelos para publicar.");
        }

        var version = ChatbotVersion.Publish(Id, TenantId, _versions.Count + 1, DraftFlowJson, DraftLayoutJson, by, now);
        _versions.Add(version);
        PublishedVersionId = version.Id;
        if (Status == ChatbotStatus.Draft)
        {
            Status = ChatbotStatus.Active;
        }

        foreach (var connection in _connections)
        {
            connection.Activate();
        }

        Touch(now);
        return version;
    }

    public void Pause(DateTimeOffset now)
    {
        switch (Status)
        {
            case ChatbotStatus.Draft:
                throw new AutomationsDomainException(AutomationsErrorCodes.ChatbotNotPublished, "El chatbot todavía no se ha publicado.");
            case ChatbotStatus.Paused:
                throw new AutomationsDomainException(AutomationsErrorCodes.ChatbotAlreadyPaused, "El chatbot ya está en pausa.");
        }

        Status = ChatbotStatus.Paused;
        Touch(now);
    }

    public void Resume(DateTimeOffset now)
    {
        switch (Status)
        {
            case ChatbotStatus.Draft:
                throw new AutomationsDomainException(AutomationsErrorCodes.ChatbotNotPublished, "El chatbot todavía no se ha publicado.");
            case ChatbotStatus.Active:
                throw new AutomationsDomainException(AutomationsErrorCodes.ChatbotAlreadyActive, "El chatbot ya está activo.");
        }

        Status = ChatbotStatus.Active;
        Touch(now);
    }

    /// <summary>Reemplaza el conjunto. Un borrador no atiende a nadie (D-C13), así que sus filas quedan inactivas; en un chatbot
    /// publicado (activo o pausado) nacen activas.</summary>
    public void SetConnections(IReadOnlyList<Guid> connectionIds, DateTimeOffset now)
    {
        var wanted = connectionIds.Distinct().ToHashSet();
        _connections.RemoveAll(connection => !wanted.Contains(connection.ConnectionId));
        var active = Status != ChatbotStatus.Draft;
        foreach (var connectionId in wanted)
        {
            if (_connections.All(connection => connection.ConnectionId != connectionId))
            {
                _connections.Add(ChatbotConnection.Create(Id, TenantId, connectionId, active));
            }
        }

        Touch(now);
    }

    /// <summary>¿El borrador se editó después de la última publicación? Con <c>SaveDraft</c> y <c>Publish</c> en el mismo instante
    /// da <c>false</c>: el borrador publicado es el que está guardado.</summary>
    public bool HasDraftChanges
    {
        get
        {
            var published = PublishedVersionId is null ? null : _versions.FirstOrDefault(version => version.Id == PublishedVersionId);
            return DraftChangedSince(DraftUpdatedAt, published?.PublishedAt);
        }
    }

    /// <summary>La regla de <see cref="HasDraftChanges"/> para quien lee sin el agregado (la lista proyectada): sin publicar, siempre.</summary>
    public static bool DraftChangedSince(DateTimeOffset draftUpdatedAt, DateTimeOffset? publishedAt) =>
        publishedAt is null || draftUpdatedAt > publishedAt;

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

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.ChatbotNameInvalid, $"El nombre es obligatorio y no puede pasar de {NameMaxLength} caracteres.");
        }

        return trimmed;
    }

    private static string NormalizeDescription(string description)
    {
        var trimmed = description.Trim();
        return trimmed.Length > DescriptionMaxLength ? trimmed[..DescriptionMaxLength] : trimmed;
    }

    /// <summary>D-C6: recorta, normaliza (<see cref="TextMatch.Normalize"/>), descarta vacías y repetidas, y recién entonces cuenta.</summary>
    private static List<string> NormalizeKeywords(IReadOnlyList<string> keywords)
    {
        var result = new List<string>();
        foreach (var keyword in keywords)
        {
            var normalized = TextMatch.Normalize(keyword.Trim());
            if (normalized.Length == 0 || result.Contains(normalized))
            {
                continue;
            }

            if (normalized.Length > KeywordMaxLength)
            {
                throw new AutomationsDomainException(
                    AutomationsErrorCodes.ChatbotKeywordTooLong, $"Cada palabra clave puede tener hasta {KeywordMaxLength} caracteres.");
            }

            result.Add(normalized);
        }

        if (result.Count > MaxKeywords)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.ChatbotKeywordsTooMany, $"Puedes definir hasta {MaxKeywords} palabras clave.");
        }

        return result;
    }
}
