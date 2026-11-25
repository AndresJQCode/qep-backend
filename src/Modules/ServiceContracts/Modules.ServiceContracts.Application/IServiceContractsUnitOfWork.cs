namespace Modules.ServiceContracts.Application;

public interface IServiceContractsUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita <c>READ COMMITTED</c> sobre la conexión del módulo: la que sostiene los candados de fila de
    /// <see cref="IIpcRateRepository"/> hasta que se guarda. Los <see cref="SaveChangesAsync"/> que corran dentro quedan en ella.
    /// Sin confirmarla, se revierte al liberarla.
    /// </summary>
    Task<IServiceContractsTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Suelta todo lo que el contexto sigue: la importación guarda fila por fila, y una fila que falló no puede arrastrar sus entidades a la siguiente.</summary>
    void Clear();

    /// <summary>
    /// Corre un ítem de un lote cuyos ítems se confirman cada uno por su lado: una falla de base dentro de él, al leer o al
    /// guardar, sale como <c>RequestConcurrencyException</c> (<c>concurrency.conflict</c>) para que el lote la responda en ese
    /// ítem en vez de un 500 con los anteriores ya confirmados. La cancelación y lo que no es de la base pasan tal cual. Fuera
    /// de un lote así no se usa: ahí una falla de base sigue siendo un 500.
    /// </summary>
    Task<T> RunBatchItemAsync<T>(Func<Task<T>> item, CancellationToken cancellationToken);
}

public interface IServiceContractsTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
