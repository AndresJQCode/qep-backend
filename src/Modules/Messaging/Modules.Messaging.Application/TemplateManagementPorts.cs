using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3: el espejo de plantillas de una conexión. Todo método recibe <c>tenantId</c> y <c>connectionId</c>: otro tenant
/// responde igual que nada. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/>, que traduce
/// <c>UX_message_templates_name_language</c> a <c>messaging.template.name_taken</c>, y <c>UX_message_templates_meta_id</c> y
/// <c>PK_template_mirror_syncs</c> a <c>concurrency.conflict</c> (dos refrescos o dos primeros llenados a la vez).
/// </summary>
public interface IMessageTemplateRepository
{
    /// <summary>Con tracking.</summary>
    Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken);

    /// <summary>Con tracking: el webhook y el refresco la modifican.</summary>
    Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken);

    /// <summary>Con tracking: la adopción (§4.5) la modifica.</summary>
    Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken);

    /// <summary>La única fila <c>Quotation</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-09 D1: la única fila <c>Order</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindOrderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-21: la única fila <c>Invoice</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindInvoiceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Soporte fase 2: la única fila <c>SupportReply</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindSupportReplyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Soporte fase 4b: la única fila <c>SupportFormalReply</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindSupportFormalReplyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Soporte fase 4b: la única fila <c>SupportSurvey</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindSupportSurveyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Mayorista: la única fila <c>WholesaleInvitationNotice</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindWholesaleInvitationNoticeAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Support task notices: the only <c>TaskNotice</c> row of the connection, tracked, or <c>null</c>.</summary>
    Task<MessageTemplate?> FindTaskNoticeAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    /// <summary>Agenda: la única fila <c>AppointmentReminder</c> de la conexión, con tracking, o <c>null</c>.</summary>
    Task<MessageTemplate?> FindAppointmentReminderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas, con tracking, por nombre e idioma en orden ordinal: el refresco las recorre y las modifica.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Sólo <c>APPROVED</c>, sin tracking: el catálogo de envío.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(MessageTemplate messageTemplate);

    void Remove(MessageTemplate messageTemplate);

    /// <summary>D-T12: una campaña <c>Scheduled</c> o <c>Sending</c> de la conexión usa ese nombre en ese idioma.</summary>
    Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17: si alguna plantilla guardada del tenant (de cualquier conexión) tiene ese archivo como imagen del encabezado.
    /// Lee la base, no lo rastreado: una fila agregada y sin guardar no cuenta. Crear la usa para no tomar ni liberar la imagen de otra.</summary>
    Task<bool> IsHeaderImageInUseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-20: guarda la imagen que una plantilla dejó de usar al cambiarla. Cuenta como en uso hasta que se borre la plantilla.</summary>
    void AddRetiredHeaderImage(MessageTemplateRetiredHeaderImage image);

    /// <summary>Spec 2026-11-20: los archivos retirados de la plantilla, para liberarlos todos al borrarla.</summary>
    Task<IReadOnlyList<Guid>> ListRetiredHeaderImageFileIdsAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken);

    /// <summary>§4.1 <c>template_mirror_syncs</c>: cuándo se confirmó el espejo de la conexión contra Meta; <c>null</c> si nunca.</summary>
    Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Upsert del marcador, con tracking; se escribe con el <c>SaveChanges</c> del refresco.</summary>
    Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-14 §4.3 y §5: la señal para el consumidor Quotations. Devuelve la fila <c>Quotation</c> en <c>APPROVED</c> de esa
/// conexión del tenant, o <c>null</c>. Devuelve nombre e idioma y no un <c>bool</c> porque el reintento versiona el nombre
/// (<c>qep_cotizacion</c>, <c>qep_cotizacion_v2</c>…; enmienda del owner, 2026-10-15): el sender de Meta manda el nombre de la fila, no
/// una constante. El adaptador hacia Quotations va en Bootstrapper cuando ese spec llegue.</summary>
public interface IQuotationTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-09 D5: la señal para el envío del pedido por Meta, hermana de <see cref="IQuotationTemplateReadiness"/>. Devuelve la
/// fila <c>Order</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>; la lee el directorio de canales (D6) y el sender al
/// enviar (D9).</summary>
public interface IOrderTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-21: la señal para el envío de la factura por Meta, hermana de <see cref="IOrderTemplateReadiness"/>. Devuelve la
/// fila <c>Invoice</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>.</summary>
public interface IInvoiceTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Soporte fase 2: la señal de la plantilla «Aviso de respuesta», hermana de <see cref="IOrderTemplateReadiness"/>. Devuelve la fila
/// <c>SupportReply</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>; la leen las opciones de respuesta de un ticket y el
/// envío por plantilla (<c>ISupportReplyOutbound</c>).</summary>
public interface ISupportReplyTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Soporte fase 4b (H10): la señal de la plantilla «Respuesta formal», hermana de <see cref="ISupportReplyTemplateReadiness"/>. Devuelve
/// la fila <c>SupportFormalReply</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>.</summary>
public interface ISupportFormalReplyTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Soporte fase 4b (H16): la señal de la plantilla de la encuesta, hermana de <see cref="ISupportReplyTemplateReadiness"/>. Devuelve la
/// fila <c>SupportSurvey</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>.</summary>
public interface ISupportSurveyTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Mayorista: la señal de la plantilla del aviso de invitación de distribuidor, hermana de <see cref="ISupportReplyTemplateReadiness"/>.
/// Devuelve la fila <c>WholesaleInvitationNotice</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>; la leen las opciones de
/// conexión de Mayorista y el envío del aviso (<c>IWholesaleInvitationNoticeOutbound</c>).</summary>
public interface IWholesaleInvitationNoticeReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Support task notices: the readiness signal of the task notice template, sibling of <see cref="IWholesaleInvitationNoticeReadiness"/>.
/// <c>FindApprovedAsync</c> returns the <c>TaskNotice</c> row when it is <c>APPROVED</c> on that connection, or <c>null</c>;
/// <c>FindStatusAsync</c> returns Meta's raw status of that row, or <c>null</c> when there is none.</summary>
public interface ITaskNoticeTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Agenda: la señal de la plantilla del recordatorio de cita, hermana de <see cref="IWholesaleInvitationNoticeReadiness"/>. Devuelve la
/// fila <c>AppointmentReminder</c> en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>; la leen el envío del recordatorio
/// (<c>IAppointmentReminderOutbound</c>) y su adaptador de la Agenda. <c>FindStatusAsync</c> devuelve el estado de Meta de esa fila
/// (aprobada o no), o <c>null</c> si la conexión no la tiene: lo lee el adaptador de los ajustes de la Agenda.</summary>
public interface IAppointmentReminderReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<AppointmentReminderTemplateStatus?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>El estado de Meta de la fila <c>AppointmentReminder</c> de una conexión, con el nombre de Meta (<c>APPROVED</c>, <c>PENDING</c>,
/// <c>REJECTED</c>…) y su motivo, si lo hay.</summary>
public sealed record AppointmentReminderTemplateStatus(string Status, string? StatusReason);

/// <summary>Cobros recurrentes §5: la señal para el consumidor Billing, hermana de <see cref="IQuotationTemplateReadiness"/>. Devuelve la fila de ese
/// <paramref name="kind"/> (<c>BillingReminder</c> o <c>BillingOverdue</c>) en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>. Devuelve
/// nombre e idioma y no un <c>bool</c> porque el reintento versiona el nombre (<c>qep_cobro_recordatorio_v2</c>…).</summary>
public interface IBillingTemplateReadiness
{
    Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, MessageTemplateKind kind, CancellationToken cancellationToken);
}

/// <summary>La fila aprobada de una plantilla del sistema, con lo que hace falta para armar su envío: el nombre vigente, el idioma, la
/// categoría, el formato guardado y los <c>components</c> crudos. Reemplaza a las dos de antes (cotización y cobros), que sólo llevaban
/// nombre e idioma y obligaban a que las variables fueran una lista fija.</summary>
public sealed record ApprovedTemplate(string Name, string Language, string Category, string ParameterFormat, string ComponentsJson);
