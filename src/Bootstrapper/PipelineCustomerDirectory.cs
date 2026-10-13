using Modules.Customers.Application;
using Modules.Pipeline.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-13 D9: nombre y completitud por id, por el mismo puerto de Customers que usa Messaging
/// (<see cref="MessagingCustomerDirectory"/>).</summary>
internal sealed class PipelineCustomerDirectory(ICustomerWhatsAppDirectory whatsApp) : IPipelineCustomerDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        customerIds.Count == 0
            ? new Dictionary<Guid, PipelineCustomerRef>()
            : (await whatsApp.FindRefsAsync(tenantId, customerIds, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => new PipelineCustomerRef(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete));
}
