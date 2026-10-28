using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record DeleteConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<bool>;

/// <summary>Spec 2026-10-08, <c>DELETE /connections/{id}</c> con If-Match: borra la fila y, en cascada,
/// sus secretos (D9). La confirmación escribiendo el nombre es de la pantalla.</summary>
public sealed class DeleteConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteConnectionCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, _) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);

        var now = clock.UtcNow;
        repository.Remove(connection);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Deleted, [], now);
        eventPublisher.Publish(ConnectionEvents.Deleted, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
