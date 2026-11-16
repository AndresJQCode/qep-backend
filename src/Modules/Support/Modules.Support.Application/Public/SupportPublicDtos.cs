namespace Modules.Support.Application;

// El contrato del formulario público (plan, «Contrato para las pantallas», Públicas). Lo lee la página de soporte en el navegador,
// sin BFF: los nombres de los campos son contrato.

/// <summary>
/// Lo que la página necesita para dibujar el formulario. <see cref="Fields"/> sale de <c>SupportFormFields</c> y
/// <see cref="DocumentTypesByCountry"/> de la tabla compartida (<c>CO</c>, <c>ES</c> y <c>*</c> para el resto): la pantalla no conoce
/// ninguna de las dos reglas. <see cref="FormVersion"/> y la versión de cada documento de <see cref="Legal"/> vuelven en el radicado para
/// detectar que algo cambió mientras la persona escribía. <see cref="Attachments"/> dice si se reciben adjuntos y con qué límites.
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
    SupportPublicAttachmentsDto Attachments,
    SupportPublicOrganizationDto? Organization);

/// <summary>
/// The tenant company that answers (spec 2026-11-15 §9.3, D-6): <c>Company.Name</c>, <c>Company.DocumentType.ToWire()</c> (<c>NIT</c>,
/// <c>CC</c>, <c>CE</c>, <c>PASAPORTE</c>, <c>NIF</c>, <c>NIE</c>, <c>TAX_ID</c>) and <c>Company.TaxId</c>. Null = the tenant chose
/// none. A deactivated company is still shown. <c>GET /form</c> takes it from the published version, <c>GET /consult</c> from the
/// current one even if unpublished.
/// </summary>
public sealed record SupportPublicOrganizationDto(string Name, string DocumentTypeCode, string DocumentNumber);

/// <summary>
/// Los adjuntos del formulario (spec §9.3; owner, 2026-11-15). Con <see cref="Allowed"/> en false la página no dibuja el control: el
/// tenant no los recibe y las rutas de adjuntos responden 422 <c>support.public.attachments_disabled</c>. <see cref="MaxFiles"/>,
/// <see cref="MaxSizeBytes"/> y <see cref="MimeTypes"/> salen de <c>SupportPublicAttachmentRules</c> para que la página arme su
/// <c>accept</c> y sus avisos con la misma regla que valida el backend, sin una copia propia que se quede atrás cuando la lista cambie
/// (por ejemplo, el día que se acepte Word con ClamAV prendido). <c>image/jpg</c> no viene: el backend lo toma como <c>image/jpeg</c>.
/// </summary>
public sealed record SupportPublicAttachmentsDto(bool Allowed, int MaxFiles, long MaxSizeBytes, IReadOnlyList<string> MimeTypes);

/// <summary>
/// Lo que la página de consulta necesita para dibujarse sola (owner, 2026-11-15, E1). Existe aparte de <see cref="SupportPublicFormDto"/>
/// porque la consulta funciona aunque el tenant nunca haya publicado su formulario, y <c>GET /form</c> responde 404 en ese caso: sin esta
/// ruta la página no tendría de dónde sacar el nombre, el logo ni el color. <see cref="AccentColor"/> es el de la versión vigente del
/// formulario, publicada o no; null si no hay ninguna, y la página usa su color por defecto.
/// </summary>
public sealed record SupportPublicConsultDto(
    string TenantName, string? LogoUrl, string? AccentColor, SupportPublicOrganizationDto? Organization);

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

/// <summary>
/// The current version of the three legal documents (spec 2026-11-15 §6.3 and §9): what the visitor reads and accepts when filing. Each
/// <c>ContentHtml</c> arrives sanitized and is painted as HTML; the portal does not sanitize or transform it again. Each <c>Version</c>
/// goes back in the filing. <see cref="CharacterizationNotice"/> is plain text, fixed and the same for every tenant, for the foot of the
/// requester's data step.
/// </summary>
public sealed record SupportPublicLegalDto(
    SupportPublicPrivacyNoticeDto PrivacyNotice,
    SupportPublicLegalDocumentDto AnonymousNotice,
    SupportPublicLegalDocumentDto Terms,
    string CharacterizationNotice);

/// <summary>The privacy notice. <see cref="AuthorizationQuestion"/> is already resolved: the tenant's or «¿Autorizas a {tenant name}
/// para el tratamiento de los datos personales suministrados en este formulario?». <see cref="PolicyUrl"/> null means the step does
/// not show «Política de tratamiento de datos».</summary>
public sealed record SupportPublicPrivacyNoticeDto(int Version, string ContentHtml, string AuthorizationQuestion, string? PolicyUrl);

/// <summary>The anonymous notice or the terms: version and sanitized HTML.</summary>
public sealed record SupportPublicLegalDocumentDto(int Version, string ContentHtml);

public sealed record SupportPublicGeoListDto(IReadOnlyList<SupportPublicGeoItemDto> Items);

public sealed record SupportPublicGeoItemDto(Guid Id, string Name);

/// <summary>
/// El radicado del formulario. Todo anulable, para que su ausencia sea un 422 <c>validation.failed</c> con el campo. Con
/// <see cref="Anonymous"/> no viaja solicitante. Cada camino manda las versiones de sus documentos (spec 2026-11-15 §9, plan L16): con
/// datos <see cref="PrivacyNoticeVersion"/> y <see cref="TermsVersion"/>, anónimo <see cref="AnonymousNoticeVersion"/> y
/// <see cref="TermsVersion"/>; la del otro camino va null. <see cref="AcceptsTerms"/> en los dos; <see cref="AcceptsDataPolicy"/> se
/// ignora en el anónimo. Las versiones y <see cref="FormVersion"/> son las que la página cargó: si cambiaron, 422 y la página recarga.
/// <see cref="Attachments"/> son los archivos ya completados con <c>available</c>, hasta cinco (plan F35); también para un anónimo.
/// <see cref="ClientRequestId"/> es opcional (spec 2026-11-15 §9.2, plan L27): un UUID por intento de radicar, el mismo en cada
/// reintento; con él, un reintento responde el ticket ya radicado en vez de crear otro.
/// </summary>
public sealed record SupportPublicFileRequest(
    Guid? TicketTypeId,
    string? Subject,
    string? Description,
    bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy,
    int? PrivacyNoticeVersion,
    int? AnonymousNoticeVersion,
    bool AcceptsTerms,
    int? TermsVersion,
    int? FormVersion,
    string? TurnstileToken,
    IReadOnlyList<SupportPublicAttachmentRef>? Attachments = null,
    Guid? ClientRequestId = null);

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
/// <see cref="PinEmailed"/> dice si el correo de apertura salió; la pantalla lo muestra igual. <see cref="ReceivedAt"/>,
/// <see cref="TermBusinessDays"/> y <see cref="TermIsLegal"/> son la fecha de radicación y la foto del término del tipo en el ticket
/// (spec 2026-11-15 §9.1, plan L26): editar el tipo después no los cambia. <see cref="TermBusinessDays"/> null = el tipo no tiene
/// término. <see cref="Pin"/> null sólo en un reintento cuyo PIN ya salió por correo (plan L28).</summary>
public sealed record SupportPublicFiledDto(
    string Number,
    string? Pin,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    int? TermBusinessDays,
    bool TermIsLegal,
    string? SuccessMessage,
    bool PinEmailed,
    string ConsultUrl);

/// <summary>La consulta de un ticket: radicado (con o sin prefijo), PIN y el token de Turnstile. El PIN viaja en el cuerpo y no en la
/// URL, para que no quede en logs (plan F9).</summary>
public sealed record SupportPublicLookupRequest(string? Number, string? Pin, string? TurnstileToken);

/// <summary>«Reenviar PIN»: sólo el radicado. El destino es el registrado en el ticket, nunca uno del cuerpo (plan F10).</summary>
public sealed record SupportPublicResendPinRequest(string? Number, string? TurnstileToken);

/// <summary>
/// Lo que la consulta muestra (plan F13): el radicado, el tipo, el estado, la radicación, el vencimiento y cómo va el plazo
/// (<c>DueState</c> como en la consola; <c>TermIsLegal</c> decide si la página dice «plazo legal» u «objetivo»), y todas las
/// respuestas que salieron, de cualquier canal. Nada más del expediente: ni el solicitante, ni el asunto, ni la traza.
/// <c>TermBusinessDays</c> es la foto del término en el ticket (spec 2026-11-15 §9.1, plan L26); null = el tipo no tiene término.
/// Fase 4b (H20): <c>Survey</c> es la encuesta de la última resolución, para que la página pinte «Califica tu atención» sin conocer los
/// estados de la encuesta; null si el ticket nunca se resolvió.
/// </summary>
public sealed record SupportPublicTicketDto(
    string Number,
    string TypeName,
    string Status,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    string? DueState,
    bool TermIsLegal,
    int? TermBusinessDays,
    IReadOnlyList<SupportPublicReplyDto> Replies,
    SupportPublicSurveyStateDto? Survey = null);

/// <summary>Fase 4b (H20, H21): la encuesta de la última resolución en la consulta. <see cref="CanRate"/> ya resuelve todas las reglas
/// (procesada, no apagada, sin calificar, el ticket Resuelto o Cerrado y sin reabrir): la página no las repite. <see cref="Rating"/> es la
/// calificación que quedó (<c>Good</c>, <c>Regular</c>, <c>Bad</c>) o null. <see cref="Options"/> viaja completa para que la página no
/// conozca el enum.</summary>
public sealed record SupportPublicSurveyStateDto(bool CanRate, string? Rating, IReadOnlyList<string> Options);

/// <summary>Fase 4b (H19): lo que la página <c>/calificar</c> necesita para dibujarse sola desde el token del correo: el nombre, el logo
/// y el color del tenant (como <see cref="SupportPublicConsultDto"/>, porque la página no pasa por la consulta), el radicado y el tipo del
/// ticket, y el estado de la encuesta. Nada más del expediente.</summary>
public sealed record SupportPublicSurveyDto(
    string TenantName,
    string? LogoUrl,
    string? AccentColor,
    string Number,
    string TypeName,
    bool CanRate,
    string? Rating,
    IReadOnlyList<string> Options);

/// <summary>Fase 4b (H19): el token del enlace del correo, en el cuerpo y nunca en la ruta (no queda en los logs de acceso).</summary>
public sealed record SupportPublicSurveyTokenRequest(string? Token);

/// <summary>Fase 4b (H19): calificar por el enlace del correo. <c>Rating</c> es <c>Good</c>, <c>Regular</c> o <c>Bad</c>; el comentario
/// es opcional.</summary>
public sealed record SupportPublicSurveyAnswerRequest(string? Token, string? Rating, string? Comment);

/// <summary>Fase 4b (H20): calificar por la página de consulta, con la misma verificación que la consulta (radicado, PIN y
/// Turnstile).</summary>
public sealed record SupportPublicRatingRequest(
    string? Number, string? Pin, string? TurnstileToken, string? Rating, string? Comment);

/// <summary>Las tres calificaciones, en el orden de la página, por nombre.</summary>
public static class SupportPublicSurveyOptions
{
    public static IReadOnlyList<string> All { get; } = Enum.GetNames<Modules.Support.Domain.SupportSurveyRating>();
}

/// <summary>Una respuesta que salió: su texto, cuándo y por qué canal (<c>Thread</c>, <c>WhatsAppTemplate</c>, <c>Email</c>,
/// <c>Portal</c>). Fase 4b (H13): una respuesta formal trae la URL firmada de su PDF (10 minutos) y el nombre del archivo; sin documento,
/// los dos en null. Sólo los trae la consulta con el PIN: no hay otra ruta pública para bajarlo.</summary>
public sealed record SupportPublicReplyDto(
    string Text, DateTimeOffset SentAt, string Channel, string? DocumentUrl = null, string? DocumentFileName = null);
