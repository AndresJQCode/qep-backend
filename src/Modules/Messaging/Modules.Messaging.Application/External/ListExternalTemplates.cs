using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Una plantilla que se puede enviar por la API externa: lo justo para armar el cuerpo del envío. Sin texto, botones ni estado: el
/// integrador no dibuja la plantilla, la nombra y manda sus parámetros.
/// </summary>
public sealed record ExternalTemplateDto(string Name, string Language, string Category, IReadOnlyList<string> Parameters);

/// <summary><c>GET /api/v1/external/whatsapp/connections/{connectionId}/templates</c>.</summary>
public sealed record ListExternalTemplatesQuery(Guid ConnectionId) : IQuery<IReadOnlyList<ExternalTemplateDto>>;

public sealed class ListExternalTemplatesHandler(
    IApiCaller caller, ITenantModules tenantModules, IMessagingConnectionDirectory connections, IMessageTemplates templates)
    : IQueryHandler<ListExternalTemplatesQuery, IReadOnlyList<ExternalTemplateDto>>
{
    public async Task<IReadOnlyList<ExternalTemplateDto>> HandleAsync(
        ListExternalTemplatesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ExternalApiAccess.EnsureAsync(caller, tenantModules, MessagingScopes.TemplatesSend, cancellationToken);
        await ExternalApiAccess.EnsureConnectionAsync(caller, connections, query.ConnectionId, cancellationToken);

        // Del espejo local, sin refrescar contra Meta: un integrador con un bucle no puede convertir la lectura en llamadas a Graph.
        var catalog = await templates.ListAsync(caller.TenantId, query.ConnectionId, refresh: false, cancellationToken);
        return catalog
            .Where(template => template.Sendable)
            .Select(template => new ExternalTemplateDto(template.Name, template.Language, template.Category, template.Parameters))
            .ToArray();
    }
}
