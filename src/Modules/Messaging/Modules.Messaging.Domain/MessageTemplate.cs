using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-14 D-T1 y D-T7: quién creó la plantilla. <c>External</c> = desde la consola de Meta; el refresco la trae.</summary>
public enum MessageTemplateKind
{
    Free,
    Quotation,
    External,

    /// <summary>Cobros recurrentes §5: la plantilla del recordatorio de vencimiento que QEP aprovisiona por conexión.</summary>
    BillingReminder,

    /// <summary>Cobros recurrentes §5: la plantilla del aviso de cobros vencidos que QEP aprovisiona por conexión.</summary>
    BillingOverdue,
}

/// <summary>Los estados de Meta, como texto (D-T15). Sin enum ni <c>CHECK</c>: Meta agrega estados sin avisar y uno desconocido se guarda
/// tal cual para que la pantalla lo muestre crudo.</summary>
public static class MessageTemplateStatuses
{
    public const int MaxLength = 32;

    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Paused = "PAUSED";
    public const string Disabled = "DISABLED";
    public const string InAppeal = "IN_APPEAL";
    public const string PendingDeletion = "PENDING_DELETION";
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>Un listado o un webhook sin <c>status</c>/<c>event</c>: no es un error, es un dato que no vino.</summary>
    public const string Unknown = "UNKNOWN";

    // Eventos del webhook que no son estados (FromWebhookEvent).
    private const string Reinstated = "REINSTATED";
    private const string Flagged = "FLAGGED";

    /// <summary>Recorta, pasa a mayúsculas y ajusta al ancho de la columna; vacío es <see cref="Unknown"/>.</summary>
    public static string Normalize(string? status)
    {
        var trimmed = (status ?? string.Empty).Trim().ToUpperInvariant();
        if (trimmed.Length == 0)
        {
            return Unknown;
        }

        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength];
    }

    /// <summary>
    /// Revisión final F2: el estado que deja un <c>event</c> del webhook <c>message_template_status_update</c>, o <c>null</c> = no tocar la
    /// fila. Dos eventos de Meta no son estados: <c>REINSTATED</c> («ya no está marcada ni deshabilitada y se puede volver a enviar») es
    /// <see cref="Approved"/>, y <c>FLAGGED</c> («recibió feedback negativo y está en riesgo de deshabilitarse») no cambia nada porque la
    /// plantilla sigue enviable. Cualquier otro pasa por <see cref="Normalize"/>: uno desconocido se guarda crudo (§4.3). El listado del
    /// refresco no pasa por acá: trae el <c>status</c> real.
    /// </summary>
    public static string? FromWebhookEvent(string? webhookEvent)
    {
        var status = Normalize(webhookEvent);
        return status switch
        {
            Reinstated => Approved,
            Flagged => null,
            _ => status,
        };
    }

    /// <summary>§4.5: lo que cuenta como «ya existe» para la plantilla de cotización: viva en Meta, aprobada o en camino.</summary>
    public static bool IsLive(string status) => status is Pending or Approved or InAppeal;
}

/// <summary>§4.2: la regla del nombre de Meta (<c>^[a-z0-9_]{1,512}$</c>) y la reserva de la plantilla de cotización (§4.5).</summary>
public static partial class MessageTemplateName
{
    public const int MaxLength = 512;

    public const string QuotationName = "qep_cotizacion";

    public const string QuotationPrefix = "qep_cotizacion_";

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    /// <summary>Cobros recurrentes §5: <c>qep_cobro_recordatorio</c>, <c>qep_cobro_vencido</c> y sus versiones (<c>_v2</c>…).</summary>
    public const string BillingPrefix = "qep_cobro_";

    public static bool IsReserved(string? name) =>
        name is not null
        && (string.Equals(name, QuotationName, StringComparison.Ordinal)
            || name.StartsWith(QuotationPrefix, StringComparison.Ordinal)
            || name.StartsWith(BillingPrefix, StringComparison.Ordinal));

    // \z y no $: $ también acepta un salto de línea al final.
    [GeneratedRegex(@"^[a-z0-9_]{1,512}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// Spec 2026-10-14 §4.1 y §4.2: el espejo de una plantilla del WABA de una conexión. Tres escritores: <see cref="Pending"/> (creada
/// desde QEP), <see cref="ApplyStatus"/> (el webhook) y <see cref="SyncFrom"/>/<see cref="Mirrored"/> (el refresco contra Meta). Lo que
/// viene de Meta se recorta al ancho de su columna: una fila que no cabe haría fallar el commit después de haber llamado a Graph.
/// </summary>
public sealed class MessageTemplate
{
    public const int MetaTemplateIdMaxLength = 64;
    public const int LanguageMaxLength = 16;
    public const int CategoryMaxLength = 32;
    public const int ParameterFormatMaxLength = 16;
    public const int StatusReasonMaxLength = 64;

    private MessageTemplate()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string MetaTemplateId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Language { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    public string ParameterFormat { get; private set; } = string.Empty;

    public string Status { get; private set; } = MessageTemplateStatuses.Unknown;

    public string? StatusReason { get; private set; }

    /// <summary>Los <c>components</c> de Meta tal cual (jsonb); <c>TemplateCatalog</c> los normaliza al leer.</summary>
    public string ComponentsJson { get; private set; } = "[]";

    public MessageTemplateKind Kind { get; private set; }

    public Guid? CreatedByMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary><c>null</c> hasta que un refresco la confirme contra Meta.</summary>
    public DateTimeOffset? SyncedAt { get; private set; }

    public bool IsApproved => Status == MessageTemplateStatuses.Approved;

    public bool IsLive => MessageTemplateStatuses.IsLive(Status);

    /// <summary>Creada desde QEP con el <c>id</c> que Meta devolvió. Una <c>Free</c> cumple la regla del nombre y no toma uno reservado;
    /// la <c>Quotation</c> trae el nombre de su definición.</summary>
    public static MessageTemplate Pending(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string? status, string componentsJson, MessageTemplateKind kind, Guid? createdByMemberId, DateTimeOffset now)
    {
        if (kind == MessageTemplateKind.External)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A template created from QEP is Free, Quotation or Billing.");
        }

        if (!MessageTemplateName.IsValid(name))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameInvalid, "The template name takes 1 to 512 lowercase letters, digits or underscores.");
        }

        if (kind == MessageTemplateKind.Free && MessageTemplateName.IsReserved(name))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameReserved, "That name is reserved for the quotation template QEP provisions.");
        }

        var template = New(tenantId, connectionId, metaTemplateId, name, language, category, parameterFormat, componentsJson, now);
        template.Kind = kind;
        template.CreatedByMemberId = createdByMemberId;
        template.Status = MessageTemplateStatuses.Normalize(status);
        return template;
    }

    /// <summary>Traída por el refresco (D-T7): <c>External</c>, ya sincronizada. El nombre es asunto de Meta y no se valida.</summary>
    public static MessageTemplate Mirrored(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string? status, string? statusReason, string componentsJson, DateTimeOffset now)
    {
        var template = New(tenantId, connectionId, metaTemplateId, name, language, category, parameterFormat, componentsJson, now);
        template.Kind = MessageTemplateKind.External;
        template.Status = MessageTemplateStatuses.Normalize(status);
        template.StatusReason = Reason(statusReason);
        template.SyncedAt = now;
        return template;
    }

    /// <summary>El webhook <c>message_template_status_update</c>: estado nuevo y su motivo («NONE» = ninguno).</summary>
    public void ApplyStatus(string? status, string? reason, DateTimeOffset now)
    {
        Status = MessageTemplateStatuses.Normalize(status);
        StatusReason = Reason(reason);
        UpdatedAt = now;
    }

    /// <summary>El refresco: lo que Meta dice hoy. El <c>kind</c> se conserva (una <c>Quotation</c> sigue siéndolo).</summary>
    public void SyncFrom(string? status, string? reason, string category, string parameterFormat, string componentsJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        Status = MessageTemplateStatuses.Normalize(status);
        StatusReason = Reason(reason);
        Category = Truncate(category ?? string.Empty, CategoryMaxLength);
        ParameterFormat = Truncate(parameterFormat ?? string.Empty, ParameterFormatMaxLength);
        ComponentsJson = componentsJson;
        SyncedAt = now;
        UpdatedAt = now;
    }

    /// <summary>§4.5: una <c>qep_cotizacion</c> creada en la consola de Meta cuyo cuerpo coincide con el que el admin envió (o, sin cuerpo, con una forma compatible) pasa a ser la de QEP.</summary>
    public void AdoptAsQuotation(DateTimeOffset now)
    {
        if (Kind != MessageTemplateKind.External)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateQuotationConflict, "Only a template created outside QEP can be adopted.");
        }

        Kind = MessageTemplateKind.Quotation;
        UpdatedAt = now;
    }

    private static MessageTemplate New(
        Guid tenantId, Guid connectionId, string metaTemplateId, string name, string language, string category, string parameterFormat,
        string componentsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaTemplateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(componentsJson);
        return new MessageTemplate
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            MetaTemplateId = Truncate(metaTemplateId, MetaTemplateIdMaxLength),
            Name = Truncate(name, MessageTemplateName.MaxLength),
            Language = Truncate(language, LanguageMaxLength),
            Category = Truncate(category ?? string.Empty, CategoryMaxLength),
            ParameterFormat = Truncate(parameterFormat ?? string.Empty, ParameterFormatMaxLength),
            ComponentsJson = componentsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static string? Reason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length == 0 || string.Equals(trimmed, "NONE", StringComparison.OrdinalIgnoreCase) ? null : Truncate(trimmed, StatusReasonMaxLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
