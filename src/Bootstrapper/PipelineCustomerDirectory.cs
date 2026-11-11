using Modules.Customers.Application;
using Modules.Pipeline.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-13 D9: nombre y completitud por id, por el mismo puerto de Customers que usa Messaging
/// (<see cref="MessagingCustomerDirectory"/>). El que quedó de una fusión (spec 2026-10-24 §4), por
/// <see cref="ICustomerMergeStore"/>.</summary>
internal sealed class PipelineCustomerDirectory(ICustomerWhatsAppDirectory whatsApp, ICustomerMergeStore mergeStore, ICustomerCompanyDirectory companies) : IPipelineCustomerDirectory
{
    public async Task<Guid> ResolveSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await mergeStore.FindSurvivorIdAsync(tenantId, customerId, cancellationToken) ?? customerId;

    public async Task<PipelineCompanyRef?> FindCompanyAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        // Spec 2026-11-10 §4.5: la empresa que quedó si la del vínculo se fusionó, igual que cualquier otro id que llega tarde.
        if (!(await companies.FindCompaniesAsync(tenantId, [customerId], cancellationToken)).TryGetValue(customerId, out var company))
        {
            return null;
        }

        return new PipelineCompanyRef(await ResolveSurvivorAsync(tenantId, company.Id, cancellationToken), company.Name);
    }

    public async Task<Guid?> FindCompanyIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        (await FindCompanyAsync(tenantId, customerId, cancellationToken))?.Id;

    public async Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        customerIds.Count == 0
            ? new Dictionary<Guid, PipelineCustomerRef>()
            : (await whatsApp.FindRefsAsync(tenantId, customerIds, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => new PipelineCustomerRef(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete));
}
