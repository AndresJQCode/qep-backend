using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record GetOperatorQueuesQuery(TenantId TenantId) : IQuery<OperatorQueuesDto>;

/// <summary>
/// Una cola de la plataforma. Viajan <b>todas las colas, siempre y en el mismo orden, también en cero</b>: si una cola vacía
/// desapareciera, la pantalla tendría que conocer la lista del backend para dibujar la que falta (BFF). <c>null</c> no es cero:
/// significa «no se pudo leer» (error de base o tope de tiempo) en los tres números, y además <c>OldestAgeSeconds</c> es
/// <c>null</c> en las colas que no miden edad (<c>campaign</c>). Mostrar un cero ahí diría «todo al día» cuando no se sabe.
/// <c>Queue</c> es el nombre técnico tal cual (<c>shopify_webhook</c>); el diccionario para mostrarlo lo tiene el frontend.
/// </summary>
public sealed record OperatorQueueDto(string Queue, long? Backlog, long? Retrying, double? OldestAgeSeconds);

public sealed record OperatorQueuesDto(IReadOnlyList<OperatorQueueDto> Queues);

/// <summary>Puerto de las colas de la plataforma. La implementación vive en Bootstrapper porque las mide Observability. Devuelve
/// las filas ya en orden, una por cola, con <c>null</c> donde no se pudo leer.</summary>
public interface IOperatorQueueSource
{
    Task<IReadOnlyList<OperatorQueueDto>> ListAsync(CancellationToken cancellationToken);
}

public sealed class GetOperatorQueuesHandler(
    IOperatorQueueSource source,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetOperatorQueuesQuery, OperatorQueuesDto>
{
    public async Task<OperatorQueuesDto> HandleAsync(GetOperatorQueuesQuery query, CancellationToken cancellationToken)
    {
        // La autorización va primero: un 403 no depende de lo que haya en las colas.
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.QueuesRead, operatorTenant);

        return new OperatorQueuesDto(await source.ListAsync(cancellationToken));
    }
}
