namespace Modules.Support.Application;

// El contrato del formulario público (plan, «Contrato para las pantallas», Públicas). Lo lee la página de soporte en el navegador,
// sin BFF: los nombres de los campos son contrato.

/// <summary>
/// Lo que la página necesita para dibujar el formulario. <see cref="Fields"/> sale de <c>SupportFormFields</c> y
/// <see cref="DocumentTypesByCountry"/> de la tabla compartida (<c>CO</c>, <c>ES</c> y <c>*</c> para el resto): la pantalla no conoce
/// ninguna de las dos reglas. <see cref="FormVersion"/> y <see cref="SupportPublicLegalDto.Version"/> vuelven en el radicado para
/// detectar que algo cambió mientras la persona escribía. Con <see cref="AllowAttachments"/> en false la página no dibuja el control de
/// adjuntos: el tenant no los recibe (spec §9.3) y las rutas de adjuntos responden 422 <c>support.public.attachments_disabled</c>.
/// </summary>
public sealed record SupportPublicFormDto(
    string TenantName,
    string? LogoUrl,
    string? AccentColor,
    string? IntroText,
    string DefaultCountry,
    IReadOnlyList<SupportPublicTypeDto> Types,
    SupportPublicFieldsDto Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DocumentTypesByCountry,
    SupportPublicLegalDto Legal,
    int FormVersion,
    bool AllowAttachments);

/// <summary>
/// Lo que la página de consulta necesita para dibujarse sola (owner, 2026-11-15, E1). Existe aparte de <see cref="SupportPublicFormDto"/>
/// porque la consulta funciona aunque el tenant nunca haya publicado su formulario, y <c>GET /form</c> responde 404 en ese caso: sin esta
/// ruta la página no tendría de dónde sacar el nombre, el logo ni el color. <see cref="AccentColor"/> es el de la versión vigente del
/// formulario, publicada o no; null si no hay ninguna, y la página usa su color por defecto.
/// </summary>
public sealed record SupportPublicConsultDto(string TenantName, string? LogoUrl, string? AccentColor);

/// <summary>Un tipo activo y publicado, en el orden de la consola.</summary>
public sealed record SupportPublicTypeDto(Guid Id, string Name, int? TermBusinessDays);

/// <summary>Los campos de cada variante (plan F18). Un campo oculto no viene en la lista. Con <see cref="ShowDocument"/> en false la
/// página usa <see cref="NoDocument"/>; con true, <see cref="Nit"/> si el tipo elegido es NIT y <see cref="Natural"/> si no.</summary>
public sealed record SupportPublicFieldsDto(
    bool ShowDocument,
    IReadOnlyList<SupportPublicFieldDto> Natural,
    IReadOnlyList<SupportPublicFieldDto> Nit,
    IReadOnlyList<SupportPublicFieldDto> NoDocument);

public sealed record SupportPublicFieldDto(string Key, bool Required);

/// <summary>La versión vigente de los textos legales: lo que la persona acepta al radicar.</summary>
public sealed record SupportPublicLegalDto(
    int Version, string AuthorizationText, string? PolicyText, string? PolicyUrl, string? TermsText);

public sealed record SupportPublicGeoListDto(IReadOnlyList<SupportPublicGeoItemDto> Items);

public sealed record SupportPublicGeoItemDto(Guid Id, string Name);

/// <summary>
/// El radicado del formulario. Todo anulable, para que su ausencia sea un 422 <c>validation.failed</c> con el campo. Con
/// <see cref="Anonymous"/> no viaja solicitante ni consentimiento. <see cref="LegalTextVersion"/> y <see cref="FormVersion"/> son las
/// que la página cargó: si cambiaron, 422 y la página recarga. <see cref="Attachments"/> son los archivos ya completados con
/// <c>available</c>, hasta cinco (plan F35); también para un anónimo.
/// </summary>
public sealed record SupportPublicFileRequest(
    Guid? TicketTypeId,
    string? Subject,
    string? Description,
    bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy,
    int? LegalTextVersion,
    int? FormVersion,
    string? TurnstileToken,
    IReadOnlyList<SupportPublicAttachmentRef>? Attachments = null);

/// <summary>Un adjunto del radicado: el par que devolvió <c>POST …/attachments</c>. Sin el <c>uploadKey</c> que lo abrió, el
/// <c>fileId</c> no sirve (plan F32).</summary>
public sealed record SupportPublicAttachmentRef(Guid? FileId, Guid? UploadKey);

/// <summary>Abrir una subida (plan F33): nombre, tipo y tamaño declarados. Los límites son los de <c>SupportPublicAttachmentRules</c>.</summary>
public sealed record SupportPublicAttachmentRequest(string? Name, string? MimeType, long? SizeBytes);

/// <summary>
/// La subida abierta. La página hace <c>PUT</c> del archivo a <see cref="UploadUrl"/> con <see cref="Headers"/> tal cual (van firmados),
/// después completa con <see cref="UploadKey"/> y lo manda junto con <see cref="FileId"/> al radicar. <see cref="UploadKey"/> es un
/// secreto: no se muestra ni va en la URL.
/// </summary>
public sealed record SupportPublicAttachmentSessionDto(
    Guid FileId, Guid UploadKey, string UploadUrl, IReadOnlyDictionary<string, string> Headers);

public sealed record SupportPublicAttachmentCompleteRequest(Guid? UploadKey);

/// <summary><see cref="Status"/> ∈ <c>available</c> (se puede adjuntar) o <c>rejected</c> (el contenido no es lo que dice o no pasó
/// el antivirus: la página avisa antes de radicar).</summary>
public sealed record SupportPublicAttachmentStatusDto(string Status);

/// <summary>Los datos del solicitante. Las claves son las de <see cref="SupportPublicFieldDto.Key"/>; la ciudad es
/// <see cref="CityId"/> con país <c>CO</c> y <see cref="CityName"/> con cualquier otro (plan F19). Un campo que el formulario oculta se
/// ignora.</summary>
public sealed record SupportPublicRequesterRequest(
    string? DocumentType,
    string? DocumentNumber,
    string? FirstNames,
    string? LastNames,
    string? BusinessName,
    string? Address,
    string? LandlinePhone,
    string? MobilePhone,
    string? Country,
    Guid? CityId,
    string? CityName,
    string? Email);

/// <summary>El radicado. <see cref="Pin"/> viaja sólo aquí, una vez: después sólo se puede reenviar uno nuevo al correo registrado.
/// <see cref="PinEmailed"/> dice si el correo de apertura salió; la pantalla lo muestra igual.</summary>
public sealed record SupportPublicFiledDto(
    string Number, string Pin, DateOnly? DueOn, string? SuccessMessage, bool PinEmailed, string ConsultUrl);

/// <summary>La consulta de un ticket: radicado (con o sin prefijo), PIN y el token de Turnstile. El PIN viaja en el cuerpo y no en la
/// URL, para que no quede en logs (plan F9).</summary>
public sealed record SupportPublicLookupRequest(string? Number, string? Pin, string? TurnstileToken);

/// <summary>«Reenviar PIN»: sólo el radicado. El destino es el registrado en el ticket, nunca uno del cuerpo (plan F10).</summary>
public sealed record SupportPublicResendPinRequest(string? Number, string? TurnstileToken);

/// <summary>
/// Lo que la consulta muestra (plan F13): el radicado, el tipo, el estado, la radicación, el vencimiento y cómo va el plazo
/// (<c>DueState</c> como en la consola; <c>TermIsLegal</c> decide si la página dice «plazo legal» u «objetivo»), y todas las
/// respuestas que salieron, de cualquier canal. Nada más del expediente: ni el solicitante, ni el asunto, ni la traza.
/// </summary>
public sealed record SupportPublicTicketDto(
    string Number,
    string TypeName,
    string Status,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    string? DueState,
    bool TermIsLegal,
    IReadOnlyList<SupportPublicReplyDto> Replies);

/// <summary>Una respuesta que salió: su texto, cuándo y por qué canal (<c>Thread</c>, <c>WhatsAppTemplate</c>, <c>Email</c>,
/// <c>Portal</c>).</summary>
public sealed record SupportPublicReplyDto(string Text, DateTimeOffset SentAt, string Channel);
