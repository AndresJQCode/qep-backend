using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §5.1: el catálogo de plantillas aprobadas de una conexión, normalizado. El adaptador de Infrastructure lo
/// guarda 5 minutos por <c>(tenant, conexión)</c>; <c>refresh</c> se lo salta. Una conexión que no es del tenant, no está
/// <c>Active</c> o no tiene <c>wabaId</c> lanza <c>messaging.connection_unavailable</c>; una lista que Meta no contesta,
/// <c>messaging.templates.unavailable</c>.
/// </summary>
public interface IMessageTemplates
{
    Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections</c>, con el permiso de lectura de la bandeja.</summary>
public sealed record ListMessagingConnectionsQuery(Guid TenantId) : IQuery<IReadOnlyList<MessagingConnectionDto>>;

public sealed class ListMessagingConnectionsHandler(IMessagingConnectionDirectory connections, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListMessagingConnectionsQuery, IReadOnlyList<MessagingConnectionDto>>
{
    public async Task<IReadOnlyList<MessagingConnectionDto>> HandleAsync(ListMessagingConnectionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        return await connections.ListActiveAsync(query.TenantId, cancellationToken);
    }
}

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections/{connectionId}/templates?refresh=</c>.</summary>
public sealed record ListTemplatesQuery(Guid TenantId, Guid ConnectionId, bool Refresh) : IQuery<IReadOnlyList<MessageTemplateDto>>;

public sealed class ListTemplatesHandler(IMessageTemplates templates, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListTemplatesQuery, IReadOnlyList<MessageTemplateDto>>
{
    public async Task<IReadOnlyList<MessageTemplateDto>> HandleAsync(ListTemplatesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        return await templates.ListAsync(query.TenantId, query.ConnectionId, query.Refresh, cancellationToken);
    }
}

/// <summary>Spec 2026-10-11 decisión 9: abre (o encuentra) la conversación de un cliente en una conexión. Infrastructure, SQL.</summary>
public sealed record OpenedConversation(Guid ConversationId, bool Created);

public interface IConversationOpener
{
    /// <summary>
    /// Busca la conversación del cliente en esa conexión por <c>customer_id</c>, si no por su BSUID y si no por el teléfono de una
    /// fila vieja sin BSUID; si no existe la inserta <c>Open</c>, con <c>user_id</c> = BSUID si lo tiene (si no <c>NULL</c> y
    /// <c>wa_id</c> = dígitos del teléfono), asignada a <paramref name="assignedMemberId"/> si viene (y, entonces, con su evento
    /// <c>Taken</c> y su auditoría a nombre de <paramref name="actorUserId"/>). <c>Created</c> sólo si esta llamada la insertó.
    /// Una carrera con la ingesta o con otro request cae en el <c>SELECT</c> posterior al <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    Task<OpenedConversation> OpenAsync(
        Guid tenantId, Guid connectionId, MessagingCustomerContact contact, Guid? assignedMemberId, Guid? actorUserId, DateTimeOffset now,
        CancellationToken cancellationToken);
}
