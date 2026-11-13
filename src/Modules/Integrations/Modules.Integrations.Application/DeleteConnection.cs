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
        // Sin limpieza en cascada (decisión del owner): quien la atiende tiene que soltarla primero. Un bot que publica justo después de
        // este chequeo queda con una conexión muerta, y Ai la ignora al validar y en el editor.
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
