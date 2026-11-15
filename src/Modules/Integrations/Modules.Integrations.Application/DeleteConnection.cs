using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record DeleteConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<bool>;

/// <summary>Spec 2026-10-08, <c>DELETE /connections/{id}</c> con If-Match: borra la fila y, en cascada,
/// sus secretos (D9). La confirmación escribiendo el nombre es de la pantalla. Una conexión que atiende un chatbot o un agente no se
/// borra: 409 con el ocupante (plan 2026-11-12, correcciones de la revisión).</summary>
public sealed class DeleteConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    IConnectionBotOccupancy botOccupancy,
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
        // No cascade cleanup (owner decision): whoever serves it has to release it first. The check and the delete share one
        // transaction, where the adapter takes the tenant's slot lock, the same one the bot slot sync takes first (plan 2026-11-12,
        // Task 6): a publish that started before waits for the delete, or the delete waits for it and sees its row. A bot published
        // after the commit is left with a dead connection, and Ai ignores it when validating and in the editor.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (await botOccupancy.FindAsync(command.TenantId, command.ConnectionId, cancellationToken) is { } occupant)
        {
            throw new ConnectionInUseByBotException(occupant);
        }

        var now = clock.UtcNow;
        repository.Remove(connection);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Deleted, [], now);
        eventPublisher.Publish(ConnectionEvents.Deleted, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
