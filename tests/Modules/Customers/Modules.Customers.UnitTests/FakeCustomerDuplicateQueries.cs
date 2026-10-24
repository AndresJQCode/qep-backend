using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El doble de <see cref="ICustomerDuplicateQueries"/> que comparten las pruebas de los casos de uso de clientes duplicados
/// (coincidencias de documento, anomalías). Uno solo, en su archivo: quien amplíe el puerto lo extiende aquí y no crea otro.
/// </summary>
internal sealed class FakeCustomerDuplicateQueries : ICustomerDuplicateQueries
{
    public List<Customer> Candidates { get; } = [];

    /// <summary>Cada llamada a <see cref="FindByNormalizedIdentificationAsync"/>, en orden.</summary>
    public List<(Guid TenantId, string NormalizedNumber, Guid? ExcludeCustomerId, int Take)> FindCalls { get; } = [];

    public Task<IReadOnlyList<Customer>> FindByNormalizedIdentificationAsync(
        Guid tenantId, string normalizedNumber, Guid? excludeCustomerId, int take, CancellationToken cancellationToken)
    {
        FindCalls.Add((tenantId, normalizedNumber, excludeCustomerId, take));
        return Task.FromResult<IReadOnlyList<Customer>>(Candidates.Take(take).ToArray());
    }

    /// <summary>Los grupos por clase, ya en el orden en que los devolvería el puerto.</summary>
    public Dictionary<CustomerAnomalyKind, List<CustomerAnomalyGroupRow>> AnomalyGroups { get; } = [];

    /// <summary>Los clientes de cada grupo por clave, ordenados como los devolvería el puerto.</summary>
    public Dictionary<string, List<Customer>> AnomalyMembers { get; } = [];

    public List<(Guid TenantId, CustomerAnomalyKind Kind, int Skip, int Take)> AnomalyListCalls { get; } = [];

    public List<(Guid TenantId, CustomerAnomalyKind Kind, string[] Keys, int PerGroup)> AnomalyMemberCalls { get; } = [];

    /// <summary>Sólo trae las clases con grupos: el handler es quien completa la que falta con cero.</summary>
    public Task<IReadOnlyDictionary<CustomerAnomalyKind, int>> CountAnomalyGroupsAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<CustomerAnomalyKind, int>>(
            AnomalyGroups.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Count));

    public Task<IReadOnlyList<CustomerAnomalyGroupRow>> ListAnomalyGroupsAsync(
        Guid tenantId, CustomerAnomalyKind kind, int skip, int take, CancellationToken cancellationToken)
    {
        AnomalyListCalls.Add((tenantId, kind, skip, take));
        var groups = AnomalyGroups.TryGetValue(kind, out var found) ? found : [];
        return Task.FromResult<IReadOnlyList<CustomerAnomalyGroupRow>>(groups.Skip(skip).Take(take).ToArray());
    }

    /// <summary>Como el puerto: a lo sumo <c>perGroup + 1</c> por clave.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<Customer>>> ListAnomalyGroupMembersAsync(
        Guid tenantId, CustomerAnomalyKind kind, IReadOnlyCollection<string> keys, int perGroup, CancellationToken cancellationToken)
    {
        AnomalyMemberCalls.Add((tenantId, kind, [.. keys], perGroup));
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Customer>>>(
            keys.Where(AnomalyMembers.ContainsKey).ToDictionary(
                key => key, key => (IReadOnlyList<Customer>)AnomalyMembers[key].Take(perGroup + 1).ToArray()));
    }
}
