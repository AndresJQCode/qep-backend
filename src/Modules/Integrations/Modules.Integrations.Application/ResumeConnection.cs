using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record ResumeConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion)
    : ICommand<ConnectionResponse>;

/// <summary>
/// Spec 2026-10-08, <c>POST …/resume</c> con If-Match: <b>vuelve a probar</b> (D4). Si pasa, Active. Si
/// la credencial ya no sirve, queda <c>NeedsAttention</c> —guardado, con su evento— y responde 422
/// <c>credentials_rejected</c>. Si el proveedor no responde, sigue pausada y no se guarda nada (P11).
/// </summary>
public sealed class ResumeConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ResumeConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(ResumeConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);
        connection.EnsurePaused();

        var secrets = ConnectionSecrets.ReadForTest(protector, connection);
        var result = await tester.TestAsync(provider, connection.Fields, secrets, cancellationToken);
        var now = clock.UtcNow;
        switch (result.Outcome)
        {
            case ConnectionTestOutcome.Ok:
                connection.Resume(now);
                if (result.RefreshedFields is { Count: > 0 } refreshed)
                {
                    connection.ApplyProviderFields(provider, refreshed);
                }

                ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Resumed, [], now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);

            case ConnectionTestOutcome.Unreachable:
                throw ConnectionVerification.Failure(provider, result);

            default:
                // El estado nuevo se guarda antes de responder el 422: la pantalla tiene que ver
                // "Necesita atención" al recargar.
                ConnectionTransitions.FlagNeedsAttention(auditRecorder, eventPublisher, executionContext, connection, result, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                throw ConnectionVerification.Failure(provider, result);
        }
    }
}
