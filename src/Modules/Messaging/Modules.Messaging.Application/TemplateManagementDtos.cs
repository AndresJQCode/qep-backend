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
    DateTimeOffset UpdatedAt);

/// <summary>§4.3: el estado de la plantilla de cotización de la conexión. <c>null</c> en la pantalla = no aprovisionada, botón de crear.</summary>
public sealed record QuotationTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason);

/// <summary>
/// §4.3: la pantalla de plantillas de una conexión en una llamada: la conexión (nombre y número), el estado de la plantilla de
/// cotización, cuándo se sincronizó el espejo por última vez (<c>null</c> = nunca) y todas las plantillas, sin paginar, por nombre e
/// idioma. Un WABA tiene decenas, no miles.
/// </summary>
public sealed record TemplateManagementDto(
    MessagingConnectionDto Connection,
    QuotationTemplateStatusDto? QuotationTemplate,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<ManagedTemplateDto> Templates);
