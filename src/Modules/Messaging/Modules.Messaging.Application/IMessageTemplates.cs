using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §5.1: el catálogo de plantillas aprobadas de una conexión, normalizado. Desde el spec 2026-10-14 (D-T8) se lee del
/// espejo local (<see cref="MirroredTemplateCatalog"/>), que se llena la primera vez y se sincroniza con Meta con <c>refresh</c>. Una
/// conexión que no es del tenant, no está <c>Active</c> o no tiene <c>wabaId</c> lanza <c>messaging.connection_unavailable</c>; una
/// lista que Meta no contesta, <c>messaging.templates.unavailable</c>.
/// </summary>
public interface IMessageTemplates
{
    Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections</c>, con el permiso de lectura de la bandeja.</summary>
public sealed record ListMessagingConnectionsQuery(Guid TenantId) : IQuery<IReadOnlyList<MessagingConnectionDto>>;

public sealed class ListMessagingConnectionsHandler(
    IMessagingConnectionDirectory connections, IBusinessHours businessHours, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListMessagingConnectionsQuery, IReadOnlyList<MessagingConnectionDto>>
{
    public async Task<IReadOnlyList<MessagingConnectionDto>> HandleAsync(ListMessagingConnectionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var active = await connections.ListActiveAsync(query.TenantId, cancellationToken);
        // Spec 2026-10-21: una sola carga de horarios para todas las conexiones, no una consulta por fila.
        var statuses = await businessHours.GetStatusesAsync(query.TenantId, active.Select(connection => connection.Id).ToArray(), cancellationToken);
        return active
            .Select(connection => statuses.TryGetValue(connection.Id, out var status)
                ? connection with { BusinessHoursScheduleId = status.ScheduleId, BusinessHoursName = status.ScheduleName, OpenNow = status.IsOpen }
                : connection)
            .ToArray();
    }
}

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections/{connectionId}/templates?refresh=</c>.</summary>
public sealed record ListTemplatesQuery(Guid TenantId, Guid ConnectionId, bool Refresh) : IQuery<IReadOnlyList<MessageTemplateDto>>;

public sealed class ListTemplatesHandler(
    IMessageTemplates templates, IMessagingConnectionDirectory connections, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListTemplatesQuery, IReadOnlyList<MessageTemplateDto>>
{
    public async Task<IReadOnlyList<MessageTemplateDto>> HandleAsync(ListTemplatesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // R8: leer el espejo es lectura; refresh lo sincroniza contra Meta, así que pide el permiso de gestionar.
        var permission = query.Refresh ? MessagingPermissions.ConversationManage : MessagingPermissions.ConversationRead;
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, permission, cancellationToken);
        // Spec 2026-10-14 §5.2 (BFF): una conexión de Instagram no tiene plantillas y la lista viaja vacía, no como error: la pantalla
        // dibuja «sin plantillas» sin tener que distinguir un canal de una falla de Meta.
        if (await connections.FindChannelAsync(query.TenantId, query.ConnectionId, cancellationToken) == ConversationChannel.Instagram)
        {
            return [];
        }

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
