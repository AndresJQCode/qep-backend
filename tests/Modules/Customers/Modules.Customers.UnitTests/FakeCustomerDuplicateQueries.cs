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
}
