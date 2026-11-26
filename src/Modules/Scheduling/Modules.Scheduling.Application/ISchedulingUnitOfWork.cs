namespace Modules.Scheduling.Application;

/// <summary>Confirma los cambios de un caso de uso en una sola transacción. La implementación traduce los errores de base (la restricción
/// de exclusión de las sesiones, la concurrencia optimista) a las excepciones de aplicación: Application no referencia EF Core.</summary>
public interface ISchedulingUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita sobre la conexión del módulo. La reserva la necesita para que el <c>SELECT … FOR UPDATE</c> de la
    /// fila del recurso dure hasta el commit: sin ella el candado se suelta al terminar la sentencia. Sin confirmarla, se revierte al
    /// liberarla.
    /// </summary>
    Task<ISchedulingTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ISchedulingTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
