namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-24 §4 (F14): cuántas referencias de otros módulos tienen los fusionados. Notas, direcciones y etiquetas son de
/// Customers y las cuenta <c>ICustomerMergeStore</c>.
/// </summary>
public sealed record CustomerMergeReferenceCounts(int Quotations, int Orders, int Conversations, int PosSales, int Deals);

/// <summary>
/// Spec 2026-10-24 §4: lo que la fusión mueve fuera de Customers, en la transacción de <c>CustomersDbContext</c>. Customers
/// no referencia los otros módulos (<c>CustomersLayerTests</c>): el adaptador vive en Bootstrapper. Todo recibe
/// <c>tenantId</c>.
/// </summary>
public interface ICustomerMergeReferences
{
    /// <summary>
    /// Pasa al que queda toda referencia viva de los ids. Corre en la conexión y la transacción del <c>CustomersDbContext</c>
    /// del scope: un error de cualquier <c>UPDATE</c> deshace también lo de Customers. Sin ids no consulta. No sube la
    /// versión de nada fuera de Customers salvo la de las campañas cuya audiencia reescribe.
    /// </summary>
    Task MoveAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken);

    /// <summary>Las cinco clases de otros módulos que pasarían al que queda (la vista previa, §3.4). Sin ids no consulta.</summary>
    Task<CustomerMergeReferenceCounts> CountAsync(
        Guid tenantId, IReadOnlyCollection<Guid> mergedIds, CancellationToken cancellationToken);
}
