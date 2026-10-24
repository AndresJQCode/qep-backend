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

    /// <summary>
    /// Cuántos grupos de cada clase hay en el tenant. Una clase sin grupos puede faltar en el diccionario: el caso de uso la
    /// completa con cero.
    /// </summary>
    Task<IReadOnlyDictionary<CustomerAnomalyKind, int>> CountAnomalyGroupsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de grupos de la clase, los más grandes primero (<c>count(*) DESC</c>, clave). Un grupo son dos o más clientes
    /// del tenant con la misma clave; incluye inactivos e incompletos.
    /// </summary>
    Task<IReadOnlyList<CustomerAnomalyGroupRow>> ListAnomalyGroupsAsync(
        Guid tenantId, CustomerAnomalyKind kind, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Por clave, a lo sumo <paramref name="perGroup"/> + 1 clientes ordenados por <c>created_at</c>, <c>id</c> (la fila de más
    /// le dice al caso de uso que el grupo se cortó).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Customer>>> ListAnomalyGroupMembersAsync(
        Guid tenantId, CustomerAnomalyKind kind, IReadOnlyCollection<string> keys, int perGroup, CancellationToken cancellationToken);
}

/// <summary>Un grupo de la lista: su clave y cuántos clientes lo forman (el total, no los que viajan).</summary>
public sealed record CustomerAnomalyGroupRow(string Key, int Size);
