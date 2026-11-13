namespace Modules.Ai.Domain;

/// <summary>Spec 2026-10-29 D-A6 (<c>CK_agents_status</c>): borrador, publicado y atendiendo, o publicado y en pausa. Por nombre en la
/// base y en la API.</summary>
public enum AgentStatus
{
    Draft,
    Active,
    Paused,
}

/// <summary>
/// Spec 2026-10-29 §4.2 y §4.7: el agente del tenant, con su borrador editable (<see cref="DraftConfigJson"/>), sus versiones
/// publicadas (inmutables) y las conexiones que atiende. La ocupación de cada conexión no vive acá sino en la tabla neutral de
/// Mensajería (§4.3), que la unidad de trabajo sincroniza con <see cref="Status"/> y <see cref="Connections"/> en el mismo commit.
/// </summary>
public sealed class Agent
{
    public const int NameMaxLength = 80;

    private readonly List<AgentVersion> _versions = [];
    private readonly List<AgentConnection> _connections = [];

    private Agent()
    {
        Name = string.Empty;
        DraftConfigJson = "{}";
    }

    /// <summary>Nace en borrador con la versión 1 de concurrencia (el DEFAULT de la columna).</summary>
    public static Agent Create(Guid tenantId, string name, AgentConfig config, Guid by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Agent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = NormalizeName(name),
            Status = AgentStatus.Draft,
            DraftConfigJson = config.ToJson(),
            DraftUpdatedAt = now,
            DraftUpdatedBy = by,
            CreatedAt = now,
            CreatedBy = by,
            UpdatedAt = now,
            Version = 1,
        };
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        Touch(now);
    }

    /// <summary>D-A7: guardar siempre guarda; lo que falta para publicar lo dice el validador.</summary>
    public void SaveDraft(AgentConfig config, Guid by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        DraftConfigJson = config.ToJson();
        DraftUpdatedAt = now;
        DraftUpdatedBy = by;
        Touch(now);
    }

    /// <summary>D-A5: copia el borrador a una versión inmutable, con su proveedor y su modelo (plan 2026-11-12 C-17). D-A6: <c>Draft</c>
    /// pasa a <c>Active</c>; <c>Paused</c> se queda pausado. D-A10: desde ahora las conexiones quedan ocupadas.</summary>
    public AgentVersion Publish(IReadOnlyList<AgentIssue> issues, Guid by, DateTimeOffset now)
    {
        EnsurePublishable(issues);
        var model = DraftConfig.Model;
        var version = AgentVersion.Publish(Id, TenantId, _versions.Count + 1, DraftConfigJson, model.Provider, model.Id, by, now);
        _versions.Add(version);
        PublishedVersionId = version.Id;
        if (Status == AgentStatus.Draft)
        {
            Status = AgentStatus.Active;
        }

        Touch(now);
        return version;
    }

    /// <summary>D-A6: pausar no corta las sesiones vivas, que terminan con su versión; sólo deja de tomar conversaciones nuevas. La
    /// conexión sigue ocupada (D-A10).</summary>
    public void Pause(DateTimeOffset now)
    {
        switch (Status)
        {
            case AgentStatus.Draft:
                throw NotPublished();
            case AgentStatus.Paused:
                throw new AiDomainException(AiErrorCodes.AgentAlreadyPaused, "El agente ya está en pausa.");
        }

        Status = AgentStatus.Paused;
        Touch(now);
    }

    public void Resume(DateTimeOffset now)
    {
        switch (Status)
        {
            case AgentStatus.Draft:
                throw NotPublished();
            case AgentStatus.Active:
                throw new AiDomainException(AiErrorCodes.AgentAlreadyActive, "El agente ya está activo.");
        }

        Status = AgentStatus.Active;
        Touch(now);
    }

    /// <summary>Reemplaza el conjunto. En un borrador no ocupa nada; en un agente publicado, la unidad de trabajo reserva las nuevas y
    /// libera las quitadas en el mismo commit.</summary>
    public void SetConnections(IReadOnlyList<Guid> connectionIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        var wanted = connectionIds.Distinct().ToList();
        _connections.RemoveAll(connection => !wanted.Contains(connection.ConnectionId));
        foreach (var connectionId in wanted.Where(id => _connections.All(connection => connection.ConnectionId != id)))
        {
            _connections.Add(AgentConnection.Create(Id, TenantId, connectionId));
        }

        Touch(now);
    }

    /// <summary>¿El borrador cambió después de la última publicación? Sin publicar, siempre.</summary>
    public bool HasDraftChanges
    {
        get
        {
            var published = PublishedVersion;
            return published is null || DraftUpdatedAt > published.PublishedAt;
        }
    }

    public AgentVersion? PublishedVersion =>
        PublishedVersionId is null ? null : _versions.FirstOrDefault(version => version.Id == PublishedVersionId);

    public AgentConfig DraftConfig => AgentConfig.FromJson(DraftConfigJson);

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public AgentStatus Status { get; private set; }

    /// <summary>§4.6, serializado por <see cref="AgentConfig.ToJson"/>; jsonb en la base.</summary>
    public string DraftConfigJson { get; private set; }

    public DateTimeOffset DraftUpdatedAt { get; private set; }

    public Guid DraftUpdatedBy { get; private set; }

    /// <summary>Null mientras es borrador (<c>CK_agents_published</c>).</summary>
    public Guid? PublishedVersionId { get; private set; }

    public IReadOnlyCollection<AgentVersion> Versions => _versions;

    public IReadOnlyCollection<AgentConnection> Connections => _connections;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Concurrencia optimista con <c>If-Match</c> (428/412), como los chatbots.</summary>
    public long Version { get; private set; }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    /// <summary>422 <c>ai.agent.invalid</c> con los códigos por ruta si hay algún error; los avisos no frenan. Es la misma regla que aplica
    /// <see cref="Publish"/>, expuesta para que el handler la corra antes de buscar la cuenta del proveedor (plan 2026-11-12 C-16).</summary>
    public static void EnsurePublishable(IReadOnlyList<AgentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var errors = issues.Where(issue => issue.Severity == AgentIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new AiDomainException(
                AiErrorCodes.AgentInvalid,
                "El agente tiene errores: corrígelos para publicar.",
                errors.GroupBy(issue => issue.Path, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Select(issue => issue.Code).ToArray(), StringComparer.Ordinal));
        }
    }

    private static AiDomainException NotPublished() => new(AiErrorCodes.AgentNotPublished, "El agente todavía no se ha publicado.");

    private static string NormalizeName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new AiDomainException(
                AiErrorCodes.AgentNameInvalid, $"El nombre es obligatorio y no puede pasar de {NameMaxLength} caracteres.");
        }

        return trimmed;
    }
}

/// <summary>Spec 2026-10-29 §4.2: una publicación del agente. Inmutable: una sesión termina con la versión con que empezó (D-A5), y con
/// su proveedor (plan 2026-11-12 C-17).</summary>
public sealed class AgentVersion
{
    private AgentVersion()
    {
        ConfigJson = "{}";
        Provider = string.Empty;
        Model = string.Empty;
    }

    internal static AgentVersion Publish(
        Guid agentId, Guid tenantId, int number, string configJson, string provider, string model, Guid by, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
            TenantId = tenantId,
            Number = number,
            ConfigJson = configJson,
            Provider = provider,
            Model = model,
            PublishedAt = now,
            PublishedBy = by,
        };

    public Guid Id { get; private set; }

    public Guid AgentId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>1, 2, 3… por agente (único con <see cref="AgentId"/>).</summary>
    public int Number { get; private set; }

    public string ConfigJson { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    public Guid PublishedBy { get; private set; }
}

/// <summary>Spec 2026-10-29 §4.2: una conexión de Mensajería que el agente atiende. Referencia blanda a <c>integrations.connections</c>.
/// Quién la ocupa de verdad lo dice <c>messaging.connection_bots</c> (§4.3).</summary>
public sealed class AgentConnection
{
    private AgentConnection()
    {
    }

    internal static AgentConnection Create(Guid agentId, Guid tenantId, Guid connectionId) =>
        new() { AgentId = agentId, TenantId = tenantId, ConnectionId = connectionId };

    public Guid AgentId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }
}
