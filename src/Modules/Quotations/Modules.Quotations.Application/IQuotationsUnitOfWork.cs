namespace Modules.Quotations.Application;

public interface IQuotationsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita sobre la conexión del módulo. Hace falta cuando el comando
    /// escribe por SQL crudo antes de <see cref="SaveChangesAsync"/>: sin ella, ese SQL se
    /// autocommitea solo y una falla posterior ya no lo deshace. El caso que la motivó es el
    /// consecutivo de pedido (<see cref="IOrderNumberGenerator"/>): los números de pedido no pueden
    /// tener huecos, así que el incremento del contador y el pedido se confirman juntos o no se
    /// confirma ninguno. <see cref="SaveChangesAsync"/> se suma a esta transacción sin abrir otra.
    /// Mismo patrón que <c>ITenancyUnitOfWork.BeginUserLifecycleScopeAsync</c>; el
    /// <c>IDbContextTransaction</c> vive en Infrastructure porque Application no referencia EF.
    /// </summary>
    Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Olvida todo lo que se agregó o se leyó en esta unidad de trabajo sin guardarlo. Después de un
    /// <see cref="SaveChangesAsync"/> fallido, lo pendiente quedaría ahí y el siguiente guardado lo volvería a
    /// intentar; el caso que la motivó es la carrera de la clave externa (<c>ExternalOrderService</c>).</summary>
    void DiscardPendingChanges();
}

/// <summary>Se confirma con <see cref="CommitAsync"/>; disponerla sin commit hace rollback, que
/// además suelta los locks de fila que haya tomado (el del contador, por ejemplo).</summary>
public interface IQuotationsTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
