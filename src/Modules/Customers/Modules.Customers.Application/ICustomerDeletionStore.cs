using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.3: lo que el borrado lee y escribe en <c>customers</c>. Todo recibe <c>tenantId</c> y
/// filtra por él. Corre sobre el <c>CustomersDbContext</c> del scope, así que con la transacción de
/// <see cref="ICustomersUnitOfWork.BeginTransactionAsync"/> abierta queda dentro de ella. Sin ids no consulta.
/// </summary>
public interface ICustomerDeletionStore
{
    /// <summary>Los clientes del tenant y sin <c>MergedIntoId</c>: un fusionado no se borra por su id, lo arrastra el que quedó.</summary>
    Task<IReadOnlyList<Customer>> FindDeletableAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Final review I-2b: bloquea con <c>FOR UPDATE</c>, en orden de id, las filas de los clientes que se van a borrar. Va después
    /// de leer los bloqueos (cuando el borrado ya tiene los candados de conversación del chatbot) y antes de la foto de
    /// consentimientos: quien registra un consentimiento o lo revoca bloquea la misma fila, así que espera al borrado y nunca deja
    /// una fila sin foto. Antes de los candados de conversación cerraría un ciclo con el runner del chatbot.
    /// </summary>
    Task LockRowsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// A-5: escribe la foto (nombre y teléfono) en el consentimiento de datos de estos clientes, que se conserva con el
    /// <c>customer_id</c> colgante. Devuelve cuántas filas tocó de cada cliente (el <c>dataConsents=</c> de la auditoría);
    /// un cliente sin consentimientos puede no aparecer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> SnapshotConsentsAsync(
        Guid tenantId,
        IReadOnlyCollection<(Guid CustomerId, string Name, string? PhoneE164)> customers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Borra los clientes y, A-6, los fusionados en ellos. Devuelve, por cliente borrado, cuántos fusionados se llevó: el
    /// <c>merged=</c> de la auditoría.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
