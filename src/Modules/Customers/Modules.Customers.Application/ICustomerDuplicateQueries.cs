using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-24 §3.2 y §3.3: lecturas que no caben en <see cref="ICustomerRepository"/> porque comparan por una
/// expresión SQL. Todo recibe <c>tenantId</c>.
/// </summary>
public interface ICustomerDuplicateQueries
{
    /// <summary>
    /// Los clientes del tenant con tipo y número cuyo número normalizado es <paramref name="normalizedNumber"/>, sin
    /// <paramref name="excludeCustomerId"/>, ordenados por <c>created_at</c>, <c>id</c>, hasta <paramref name="take"/>.
    /// Incluye inactivos e incompletos.
    /// </summary>
    Task<IReadOnlyList<Customer>> FindByNormalizedIdentificationAsync(
        Guid tenantId, string normalizedNumber, Guid? excludeCustomerId, int take, CancellationToken cancellationToken);
}
