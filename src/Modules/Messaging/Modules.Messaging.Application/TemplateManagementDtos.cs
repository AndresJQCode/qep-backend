namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3: una plantilla del espejo para la pantalla de administración. BFF: <c>status</c> y <c>statusReason</c> viajan con
/// el nombre de Meta (<c>REJECTED</c>, <c>INVALID_FORMAT</c>; el diccionario en español lo tiene el frontend, D-T15); <c>header</c>,
/// <c>body</c>, <c>footer</c>, <c>buttons</c>, <c>parameters</c>, <c>sendable</c> y <c>unsupportedReason</c> salen resueltos de
/// <see cref="TemplateCatalog"/> para que la pantalla no conozca el formato de componentes de Meta. <c>createdBy</c> es <c>null</c> en
/// una <c>External</c>: nadie de QEP la creó.
/// </summary>
public sealed record ManagedTemplateDto(
    Guid Id,
    string MetaTemplateId,
    string Name,
    string Language,
    string Category,
    string ParameterFormat,
    string Status,
    string? StatusReason,
    string Kind,
    string? Header,
    string Body,
    string? Footer,
    IReadOnlyList<TemplateButtonDto> Buttons,
    IReadOnlyList<string> Parameters,
    bool Sendable,
    string? UnsupportedReason,
    MemberRefDto? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Sólo en plantillas de cobro que Meta reclasificó: la categoría que QEP pidió. Es un aviso, no frena nada.
    string? RequestedCategory = null,
    // Spec 2026-11-17 (BFF): la URL pública de la imagen fija del encabezado, para que la pantalla la muestre sin conocer Storage.
    // «header» sigue siendo sólo el texto y es null en una plantilla con imagen. null = sin imagen guardada.
    string? HeaderImageUrl = null,
    // Spec 2026-11-20 (BFF): el formato del encabezado multimedia («Image», «Video», «Document»), para que la pantalla sepa si ofrece
    // «Cambiar imagen» sin leer los componentes de Meta. null = encabezado de texto o sin encabezado.
    string? HeaderMedia = null);

/// <summary>§4.3: el estado de la plantilla de cotización de la conexión. <c>null</c> en la pantalla = no aprovisionada, botón de crear.</summary>
public sealed record QuotationTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Spec 2026-11-09 D4: el estado de la plantilla de pedido de la conexión, con la misma forma que la de cotización. <c>null</c> =
/// no aprovisionada. Un frontend viejo lo ignora.</summary>
public sealed record OrderTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Spec 2026-11-21: el estado de la plantilla de factura de la conexión, con la misma forma que la de pedido. <c>null</c> = no
/// aprovisionada. Un frontend viejo lo ignora.</summary>
public sealed record InvoiceTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Soporte fase 2: el estado de la plantilla «Aviso de respuesta» de la conexión, con la misma forma que la de pedido. <c>null</c> =
/// no aprovisionada, botón de crear. Un frontend viejo lo ignora.</summary>
public sealed record SupportReplyTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Soporte fase 4b: el estado de la plantilla «Respuesta formal» (con el PDF de encabezado), aparte de la de «Aviso de respuesta»
/// porque son dos aprovisionamientos distintos. <c>null</c> = no aprovisionada. Un frontend viejo lo ignora.</summary>
public sealed record SupportFormalReplyTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Soporte fase 4b: el estado de la plantilla de la encuesta de satisfacción. <c>null</c> = no aprovisionada. Un frontend viejo lo
/// ignora.</summary>
public sealed record SupportSurveyTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Mayorista: el estado de la plantilla del aviso de invitación de distribuidor. <c>null</c> = no aprovisionada. Un frontend viejo
/// lo ignora.</summary>
public sealed record WholesaleInvitationNoticeTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Support task notices: the status of the assignment notice template. <c>null</c> = not provisioned. An old frontend ignores it.</summary>
public sealed record TaskNoticeTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>Agenda: el estado de la plantilla del recordatorio de cita. <c>null</c> = no aprovisionada. Un frontend viejo lo ignora.</summary>
public sealed record AppointmentReminderTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>
/// §4.3: la pantalla de plantillas de una conexión en una llamada: la conexión (nombre y número), el estado de la plantilla de
/// cotización, cuándo se sincronizó el espejo por última vez (<c>null</c> = nunca) y todas las plantillas, sin paginar, por nombre e
/// idioma. Un WABA tiene decenas, no miles.
/// </summary>
public sealed record TemplateManagementDto(
    MessagingConnectionDto Connection,
    QuotationTemplateStatusDto? QuotationTemplate,
    OrderTemplateStatusDto? OrderTemplate,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<ManagedTemplateDto> Templates,
    SupportReplyTemplateStatusDto? SupportReplyTemplate,
    SupportFormalReplyTemplateStatusDto? SupportFormalReplyTemplate = null,
    SupportSurveyTemplateStatusDto? SupportSurveyTemplate = null,
    WholesaleInvitationNoticeTemplateStatusDto? WholesaleInvitationNoticeTemplate = null,
    InvoiceTemplateStatusDto? InvoiceTemplate = null,
    TaskNoticeTemplateStatusDto? TaskNoticeTemplate = null,
    AppointmentReminderTemplateStatusDto? AppointmentReminderTemplate = null);
