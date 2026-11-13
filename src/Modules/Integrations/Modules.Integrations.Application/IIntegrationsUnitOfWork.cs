namespace Modules.Integrations.Application;

/// <summary>
/// Guarda la conexión, sus secretos, su auditoría y sus eventos en una sola transacción. La
/// implementación traduce el índice único del nombre y la concurrencia (spec 2026-10-08,
/// «Proyectos»), en Infrastructure.
/// </summary>
public interface IIntegrationsUnitOfWork
{
    /// <summary>Guarda. Con una transacción abierta por <see cref="BeginTransactionAsync"/>, además la confirma: guardar es
    /// confirmar, y el candado del token se suelta ahí.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-12 §6.2: una transacción explícita, para tomar el candado del token de Shopify. Disponerla sin
    /// guardar la deshace y suelta el candado.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>El candado del token de Shopify de <paramref name="ownerId"/> (una conexión o una instalación), esperando a
    /// que se libere. Lo usa el enlace: la persona espera unos segundos en vez de recibir un error. Exige la transacción
    /// de <see cref="BeginTransactionAsync"/>.</summary>
    Task LockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Como <see cref="LockShopifyTokenAsync"/>, sin esperar: <c>false</c> si otro proceso lo tiene. Lo usa el worker,
    /// que salta la conexión y vuelve en la próxima vuelta.</summary>
    Task<bool> TryLockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken);
}
