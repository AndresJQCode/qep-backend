using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record PauseConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion)
    : ICommand<ConnectionResponse>;

/// <summary>Spec 2026-10-08, <c>POST …/pause</c> con If-Match: <c>Active → Paused</c>; si no está activa,
/// <c>not_active</c>. Publica <c>connection-paused</c> para que un consumidor la suelte.</summary>
public sealed class PauseConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<PauseConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(PauseConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);

        var now = clock.UtcNow;
        connection.Pause(now);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Paused, [], now);
        eventPublisher.Publish(ConnectionEvents.Paused, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
