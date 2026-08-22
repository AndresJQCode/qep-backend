using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class ClientClassificationRepository(CustomersDbContext dbContext)
    : IClientClassificationRepository
{
    public async Task<IReadOnlyList<ClientClassification>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.ClientClassifications
            .AsNoTracking()
            .Where(classification => classification.TenantId == tenantId)
            .OrderBy(classification => classification.Name)
            .ToListAsync(cancellationToken);

    // Con tracking a proposito, a diferencia de ListAsync: los llamadores de este mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<ClientClassification?> FindAsync(
        Guid tenantId,
        ClientClassificationId classificationId,
        CancellationToken cancellationToken) =>
        dbContext.ClientClassifications.SingleOrDefaultAsync(
            classification =>
                classification.TenantId == tenantId && classification.Id == classificationId,
            cancellationToken);

    public void Add(ClientClassification classification) =>
        dbContext.ClientClassifications.Add(classification);

    public void Remove(ClientClassification classification) =>
        dbContext.ClientClassifications.Remove(classification);
}
