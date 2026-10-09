using Modules.Integrations.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public IntegrationConnections ConnectionsPort() => new(Repository, Catalog, Modules, Protector);

    public ConnectionHealthReporter HealthReporter() => new(Repository, UnitOfWork, Audit, Events, Clock);
}
