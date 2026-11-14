namespace Modules.Support.Application;

public interface ISupportUnitOfWork
{
    /// <summary>Guarda tomando el candado compartido de cada cliente que el guardado referencia (<c>CustomerReferenceWrites</c>):
    /// un cliente que ya no existe sale como <c>support.ticket.customer_not_found</c> y no se guarda nada.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Abre una transacción explícita sobre la conexión del contexto del módulo (copia de
    /// <c>ICustomersUnitOfWork.BeginTransactionAsync</c>). El alta la usa para que el radicado y el ticket se confirmen juntos:
    /// sin confirmarla, se revierte al liberarla y el número no se pierde (plan D2).</summary>
    Task<ISupportTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ISupportTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
