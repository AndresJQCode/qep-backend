using Modules.Automations.Application;
using Modules.Integrations.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H4: las conexiones «API externa» del tenant, sobre <see cref="IIntegrationConnections"/>. Trae todas, en
/// cualquier estado: el validador distingue activas de pausadas. Mientras el proveedor <c>external-api</c> no exista (plan 2) no hay ninguna.</summary>
internal sealed class AutomationExternalApis(IIntegrationConnections connections) : IAutomationExternalApis
{
    public const string ProviderKey = "external-api";

    public async Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await connections.ListByProviderAsync(tenantId, ProviderKey, cancellationToken))
            .Select(connection => new ExternalApiRef(connection.Id, connection.Name, connection.Status.ToString()))];
}
