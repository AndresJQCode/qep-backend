using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Un número que la clave puede usar. Existe porque sin esta lista el integrador no tiene de dónde sacar el <c>connectionId</c> que
/// pide el envío (spec 2026-10-30 §5.2), y trae solo lo que necesita para elegir: id, nombre y teléfono.
/// </summary>
public sealed record ExternalConnectionDto(Guid Id, string Name, string? PhoneNumber);

/// <summary><c>GET /api/v1/external/whatsapp/connections</c>. Sin parámetros: el tenant sale de la clave, no de la ruta.</summary>
public sealed record ListExternalConnectionsQuery : IQuery<IReadOnlyList<ExternalConnectionDto>>;

public sealed class ListExternalConnectionsHandler(
    IApiCaller caller, ITenantModules tenantModules, IMessagingConnectionDirectory connections)
    : IQueryHandler<ListExternalConnectionsQuery, IReadOnlyList<ExternalConnectionDto>>
{
    public async Task<IReadOnlyList<ExternalConnectionDto>> HandleAsync(
        ListExternalConnectionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ExternalApiAccess.EnsureAsync(caller, tenantModules, MessagingScopes.TemplatesSend, cancellationToken);

        // Solo los números de WhatsApp activos del tenant de la clave, y entre ellos los que la clave puede usar.
        var active = await connections.ListActiveAsync(caller.TenantId, cancellationToken);
        return active
            .Where(connection => connection.Channel == nameof(ConversationChannel.WhatsApp) && caller.CanUseConnection(connection.Id))
            .OrderBy(connection => connection.Name, StringComparer.Ordinal)
            .Select(connection => new ExternalConnectionDto(connection.Id, connection.Name, connection.DisplayPhoneNumber))
            .ToArray();
    }
}
