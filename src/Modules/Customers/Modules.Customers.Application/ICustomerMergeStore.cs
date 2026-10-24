using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-24 §3.4 (F14): las dos clases de Customers que la vista previa cuenta fuera del agregado.</summary>
public sealed record CustomerMergeOwnedCounts(int Notes, int Addresses);

/// <summary>
/// Spec 2026-10-24 §3.2–§3.4: lo que la fusión lee y escribe en <c>customers</c> sin pasar por el agregado. Todo recibe
/// <c>tenantId</c> y filtra por él. Corre sobre el <c>CustomersDbContext</c> del scope, así que con la transacción de
/// <see cref="ICustomersUnitOfWork.BeginTransactionAsync"/> abierta queda dentro de ella. Sin ids no consulta.
/// </summary>
public interface ICustomerMergeStore
{
    /// <summary>Los clientes que hoy apuntan a alguno de estos (la cadena de D-F2: C → B cuando B se fusiona en A).</summary>
    Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>
    /// Hace que estos clientes, ya fusionados, apunten al que queda (C → A). Sólo toca filas con <c>merged_into_id</c>: no
    /// convierte en fusionado a nadie que no lo esté. No sube la versión: un fusionado no se escribe.
    /// </summary>
    Task RepointMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken);

    /// <summary>Las notas y las direcciones de envío de estos clientes (la vista previa, §3.4).</summary>
    Task<CustomerMergeOwnedCounts> CountOwnedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>
    /// Notas y direcciones pasan al que queda; las direcciones llegan con <c>is_principal = false</c> (§3.2 paso 3), así la
    /// principal del que queda sigue siendo la única. Las notas no cambian su <c>version</c>.
    /// </summary>
    Task MoveOwnedAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary><c>merged_into_id</c> del cliente; <c>null</c> si no está fusionado o no es del tenant.</summary>
    Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>
    /// El que quedó del fusionado más viejo (<c>created_at</c>, <c>id</c>) con ese par exacto; <c>null</c> si ninguno. Sólo
    /// mira fusionados: el dueño vivo del documento lo encuentra la búsqueda normal.
    /// </summary>
    Task<Guid?> FindSurvivorIdByIdentificationAsync(
        Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken);
}
