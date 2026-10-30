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

    /// <summary>Todas, con tracking, por nombre e idioma en orden ordinal: el refresco las recorre y las modifica.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Sólo <c>APPROVED</c>, sin tracking: el catálogo de envío.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(MessageTemplate messageTemplate);

    void Remove(MessageTemplate messageTemplate);

    /// <summary>D-T12: una campaña <c>Scheduled</c> o <c>Sending</c> de la conexión usa ese nombre en ese idioma.</summary>
    Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken);

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
    Task<ApprovedQuotationTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Lo que el sender de Meta necesita para mandar la cotización: el nombre vigente de la plantilla y su idioma.</summary>
public sealed record ApprovedQuotationTemplate(string Name, string Language);

/// <summary>Cobros recurrentes §5: la señal para el consumidor Billing, hermana de <see cref="IQuotationTemplateReadiness"/>. Devuelve la fila de ese
/// <paramref name="kind"/> (<c>BillingReminder</c> o <c>BillingOverdue</c>) en <c>APPROVED</c> de esa conexión del tenant, o <c>null</c>. Devuelve
/// nombre e idioma y no un <c>bool</c> porque el reintento versiona el nombre (<c>qep_cobro_recordatorio_v2</c>…).</summary>
public interface IBillingTemplateReadiness
{
    Task<ApprovedBillingTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, MessageTemplateKind kind, CancellationToken cancellationToken);
}

/// <summary>Lo que el sender de cobros necesita: el nombre vigente de la plantilla y su idioma.</summary>
public sealed record ApprovedBillingTemplate(string Name, string Language);
