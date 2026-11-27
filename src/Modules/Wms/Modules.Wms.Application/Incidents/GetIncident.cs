using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record GetIncidentQuery(Guid TenantId, Guid IncidentId) : IQuery<IncidentDto>;

/// <summary>La ficha de una novedad. Mismo patrón de acceso que el resto: tenant y permiso antes de leer, y la novedad se busca con
/// el tenant del llamador (otro tenant no llega hasta aquí: 403, nunca 404).</summary>
public sealed class GetIncidentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IIncidentRepository incidents,
    IncidentDtoComposer composer)
    : IQueryHandler<GetIncidentQuery, IncidentDto>
{
    public async Task<IncidentDto> HandleAsync(GetIncidentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);

        var incident = await incidents.GetWithEventsAsync(query.TenantId, query.IncidentId, cancellationToken)
            ?? throw WmsNotFound.Incident(query.IncidentId);
        await authorization.EnsureAsync(
            executionContext,
            query.TenantId,
            WmsPermissions.DocumentRead,
            incident.OwnerId.Value,
            incident.WarehouseId.Value,
            cancellationToken);
        return await composer.ComposeAsync(incident, cancellationToken);
    }
}
