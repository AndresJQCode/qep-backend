namespace Modules.Catalog.Application;

/// <summary>
/// Encola el aviso de que unas fotos de producto quedaron sin dueño, para que Storage purgue sus
/// archivos. Va por outbox: commitea en la misma transacción que el cambio que las liberó, o no
/// sale nada.
///
/// No hace nada con una colección vacía y deduplica los ids.
/// </summary>
public interface IProductImageReleasePublisher
{
    void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt);
}
