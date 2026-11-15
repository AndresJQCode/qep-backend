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

    /// <summary>
    /// Toma ya, dentro de la transacción abierta, el candado compartido del cliente que el alta va a referenciar, y comprueba que siga
    /// existiendo (si no, <c>support.ticket.customer_not_found</c>). Es para respetar el orden de candados frente al borrado de
    /// clientes: el del cliente antes que cualquier fila (revisión final, m1). El alta lo llama antes del radicado, cuya fila es del
    /// tenant entero: así una alta que espera a un cliente no frena las demás. <see cref="SaveChangesAsync"/> lo vuelve a tomar, y
    /// en la misma transacción eso no espera.
    /// </summary>
    Task HoldCustomerReferenceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}

public interface ISupportTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
