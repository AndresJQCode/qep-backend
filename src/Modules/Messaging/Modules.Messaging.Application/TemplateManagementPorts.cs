using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3: el espejo de plantillas de una conexión. Todo método recibe <c>tenantId</c> y <c>connectionId</c>: otro tenant
/// responde igual que nada. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/>, que traduce
/// <c>UX_message_templates_name_language</c> a <c>messaging.template.name_taken</c> y <c>UX_message_templates_meta_id</c> a
/// <c>concurrency.conflict</c> (dos refrescos a la vez).
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

/// <summary>Spec 2026-10-14 §4.3 y §5: la señal para el consumidor Quotations. <c>true</c> sólo con una fila <c>Quotation</c> en
/// <c>APPROVED</c> para esa conexión del tenant. El adaptador hacia Quotations va en Bootstrapper cuando ese spec llegue.</summary>
public interface IQuotationTemplateReadiness
{
    Task<bool> IsApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
