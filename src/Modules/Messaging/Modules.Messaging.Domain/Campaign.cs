namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-11 §6.1. Viaja por nombre; en la columna <c>status</c> también.</summary>
public enum CampaignStatus
{
    Draft,
    Scheduled,
    Sending,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>§6.1: <c>Pending → Sending → Sent | Failed</c>, o <c>Skipped</c>. <c>Sent</c> es «Meta lo aceptó»; entregado y leído salen
/// del <c>status</c> del mensaje.</summary>
public enum CampaignRecipientStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    Skipped,
}

/// <summary>De dónde sale el valor de una variable de la plantilla: un texto fijo para toda la audiencia, el nombre de cada cliente, o el
/// saludo de la hora en que sale cada envío.</summary>
public enum CampaignParameterSource
{
    Text,
    CustomerName,
    Greeting,
}

/// <summary>
/// Una variable de la plantilla, como viaja en la API y en <c>parameters jsonb</c>: <c>{ "source": "text", "value": "…" }</c> o
/// <c>{ "source": "customerName" }</c> o <c>{ "source": "greeting" }</c>. <c>Value</c> sólo existe para <c>text</c>.
/// </summary>
public sealed record CampaignParameter(CampaignParameterSource Source, string? Value)
{
    public static CampaignParameter CustomerName { get; } = new(CampaignParameterSource.CustomerName, null);

    public static CampaignParameter Greeting { get; } = new(CampaignParameterSource.Greeting, null);

    public static CampaignParameter Text(string value) => new(CampaignParameterSource.Text, value);

    public const string TextWire = "text";
    public const string CustomerNameWire = "customerName";
    public const string GreetingWire = "greeting";

    public string SourceWire => Source switch
    {
        CampaignParameterSource.Text => TextWire,
        CampaignParameterSource.Greeting => GreetingWire,
        _ => CustomerNameWire,
    };

    public static bool TryParseSource(string? wire, out CampaignParameterSource source)
    {
        source = default;
        switch (wire)
        {
            case TextWire:
                source = CampaignParameterSource.Text;
                return true;
            case CustomerNameWire:
                source = CampaignParameterSource.CustomerName;
                return true;
            case GreetingWire:
                source = CampaignParameterSource.Greeting;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Spec 2026-10-11 §6.1: una campaña de plantillas de WhatsApp a una audiencia (etiquetas más clientes sueltos), con
/// <see cref="Version"/> como token de concurrencia. Aquí viven la máquina de estados que el usuario mueve —
/// <c>Draft → Scheduled | Sending</c> al lanzar y <c>Scheduled | Sending → Cancelled</c> al cancelar— y las reglas de entrada. El
/// resto de las transiciones (<c>Scheduled → Sending</c> cuando llega la hora, <c>Sending → Completed | Failed</c>) las hace el
/// worker por SQL, con la versión como guarda, porque ocurren sin una persona y sin cargar el agregado. La audiencia se congela al
/// empezar el envío (decisión 13), no aquí.
/// </summary>
public sealed class Campaign
{
    public const int NameMaxLength = 120;

    /// <summary>§6.2: tope de clientes sueltos; el resto de la audiencia son etiquetas.</summary>
    public const int MaxLooseCustomers = 500;

    /// <summary>§6.2: una campaña no se programa más lejos que esto.</summary>
    public static readonly TimeSpan MaxScheduleAhead = TimeSpan.FromDays(30);

    private Campaign()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string TemplateName { get; private set; } = string.Empty;

    public string TemplateLanguage { get; private set; } = string.Empty;

    public string TemplateCategory { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, CampaignParameter> Parameters { get; private set; } = new Dictionary<string, CampaignParameter>();

    public IReadOnlyList<Guid> AudienceTagIds { get; private set; } = [];

    public IReadOnlyList<Guid> AudienceCustomerIds { get; private set; } = [];

    public CampaignStatus Status { get; private set; }

    /// <summary>Texto en español de por qué se detuvo (<see cref="CampaignStatus.Failed"/>); lo escribe el worker.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>El código que lo detuvo (<c>132015</c>, <c>190</c>, <c>connection_unavailable</c>…); lo escribe el worker.</summary>
    public string? FailureCode { get; private set; }

    public DateTimeOffset? ScheduledAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Cuándo el worker terminó de escribir los destinatarios. Hasta entonces la campaña no se cierra: sin esta marca, una
    /// audiencia que se cayó a medias parecería terminada.</summary>
    public DateTimeOffset? AudienceFrozenAt { get; private set; }

    /// <summary>Cuántas veces se reencolaron sus fallidos (<see cref="Retry"/>). Cero para una campaña que sólo se lanzó.</summary>
    public int RetryCount { get; private set; }

    public Guid CreatedByMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsEditable => Status == CampaignStatus.Draft;

    public bool IsCancellable => Status is CampaignStatus.Scheduled or CampaignStatus.Sending;

    /// <summary>Sólo una campaña que ya terminó de enviar —completa o detenida— reencola sus fallidos; una cancelada no, porque alguien
    /// decidió frenarla.</summary>
    public bool IsRetriable => Status is CampaignStatus.Completed or CampaignStatus.Failed;

    public static Campaign Create(
        Guid id,
        Guid tenantId,
        Guid connectionId,
        string name,
        string templateName,
        string templateLanguage,
        string templateCategory,
        IReadOnlyDictionary<string, CampaignParameter> parameters,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        DateTimeOffset? scheduledAt,
        Guid createdByMemberId,
        DateTimeOffset now)
    {
        var campaign = new Campaign
        {
            Id = id,
            TenantId = tenantId,
            CreatedByMemberId = createdByMemberId,
            Status = CampaignStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        campaign.Apply(name, connectionId, templateName, templateLanguage, templateCategory, parameters, tagIds, customerIds, scheduledAt, now);
        return campaign;
    }

    /// <summary>Sólo un borrador se edita (el handler responde 409 antes de llegar acá); reemplaza todo lo que trae
    /// <c>CampaignInput</c>.</summary>
    public void Update(
        string name,
        Guid connectionId,
        string templateName,
        string templateLanguage,
        string templateCategory,
        IReadOnlyDictionary<string, CampaignParameter> parameters,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        DateTimeOffset? scheduledAt,
        DateTimeOffset now)
    {
        RequireStatus(IsEditable);
        Apply(name, connectionId, templateName, templateLanguage, templateCategory, parameters, tagIds, customerIds, scheduledAt, now);
        Version++;
    }

    /// <summary>R7: al lanzar se guarda la categoría que el catálogo tiene <b>ahora</b>; Meta puede reclasificar una plantilla entre que
    /// se guardó el borrador y se lanzó, y de eso depende si el opt-out excluye. Sólo en un borrador; no sube la versión (la sube
    /// <see cref="Launch"/> en la misma operación).</summary>
    public void UseLiveTemplateCategory(string templateCategory)
    {
        RequireStatus(IsEditable);
        TemplateCategory = templateCategory;
    }

    /// <summary>Con una fecha futura queda <see cref="CampaignStatus.Scheduled"/>; sin fecha empieza ya. Una fecha que ya pasó desde
    /// que se guardó el borrador no se envía de golpe: se pide corregirla.</summary>
    public void Launch(DateTimeOffset now)
    {
        RequireStatus(IsEditable);
        if (ScheduledAt is { } at)
        {
            if (at <= now)
            {
                throw new MessagingDomainException(MessagingErrorCodes.CampaignInvalid, "The scheduled time already passed; edit the draft.");
            }

            Status = CampaignStatus.Scheduled;
        }
        else
        {
            Status = CampaignStatus.Sending;
            StartedAt = now;
        }

        Touch(now);
    }

    /// <summary>Lo que ya está en vuelo termina; los pendientes los pasa a <c>Skipped</c> quien guarda (spec §6.3).</summary>
    public void Cancel(DateTimeOffset now)
    {
        RequireStatus(IsCancellable);
        Status = CampaignStatus.Cancelled;
        CompletedAt = now;
        Touch(now);
    }

    /// <summary>
    /// Vuelve a <see cref="CampaignStatus.Sending"/> para que el worker envíe de nuevo a los fallidos que quien guarda pasa a
    /// <c>Pending</c>. La audiencia sigue congelada: no se recalcula, se reintenta a los mismos. Se borra el motivo de la detención y se
    /// guarda la categoría que el catálogo tiene ahora (R7), como al lanzar.
    /// </summary>
    public void Retry(string templateCategory, DateTimeOffset now)
    {
        RequireStatus(IsRetriable);
        TemplateCategory = templateCategory;
        Status = CampaignStatus.Sending;
        FailureReason = null;
        FailureCode = null;
        CompletedAt = null;
        RetryCount++;
        Touch(now);
    }

    private void Apply(
        string name,
        Guid connectionId,
        string templateName,
        string templateLanguage,
        string templateCategory,
        IReadOnlyDictionary<string, CampaignParameter> parameters,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        DateTimeOffset? scheduledAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(tagIds);
        ArgumentNullException.ThrowIfNull(customerIds);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw Invalid($"The name takes 1 to {NameMaxLength} characters.");
        }

        var tags = tagIds.Distinct().ToArray();
        var customers = customerIds.Distinct().ToArray();
        if (tags.Length == 0 && customers.Length == 0)
        {
            throw Invalid("The audience cannot be empty.");
        }

        if (customers.Length > MaxLooseCustomers)
        {
            throw Invalid($"At most {MaxLooseCustomers} loose customers.");
        }

        if (scheduledAt is { } at && (at <= now || at > now.Add(MaxScheduleAhead)))
        {
            throw Invalid("The schedule has to be in the future and within 30 days.");
        }

        foreach (var (key, parameter) in parameters)
        {
            if (string.IsNullOrEmpty(key) || (parameter.Source == CampaignParameterSource.Text && string.IsNullOrWhiteSpace(parameter.Value)))
            {
                throw Invalid("A text variable needs a value.");
            }
        }

        Name = trimmed;
        ConnectionId = connectionId;
        TemplateName = templateName;
        TemplateLanguage = templateLanguage;
        TemplateCategory = templateCategory;
        Parameters = new Dictionary<string, CampaignParameter>(parameters, StringComparer.Ordinal);
        AudienceTagIds = tags;
        AudienceCustomerIds = customers;
        ScheduledAt = scheduledAt;
        UpdatedAt = now;
    }

    private static MessagingDomainException Invalid(string message) => new(MessagingErrorCodes.CampaignInvalid, message);

    private static void RequireStatus(bool allowed)
    {
        if (!allowed)
        {
            throw new InvalidOperationException("The campaign is not in a state that allows this change; the handler checks first.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
