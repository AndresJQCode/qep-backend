using BuildingBlocks.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record TestConnectionCommand(Guid TenantId, Guid ConnectionId) : ICommand<ConnectionResponse>;

/// <summary>
/// Spec 2026-10-08, <c>POST /connections/{id}/test</c>: la misma prueba con lo guardado. Sin
/// <c>If-Match</c> (P27). Siempre responde la conexión (P10): si pasa, <c>last_verified_at</c> y
/// <c>NeedsAttention → Active</c>. Si el proveedor rechaza la credencial de una conexión
/// <c>Active</c>, pasa a <c>NeedsAttention</c> con su evento y su auditoría, igual que el reporter
/// (DECISIÓN 2, owner 2026-10-08, coherente con D2). Cualquier otra falla —o un rechazo sobre una
/// <c>Paused</c> o una que ya estaba en <c>NeedsAttention</c>— sólo anota <c>last_failure_*</c>.
/// Audita siempre <c>verified</c> con su <c>outcome</c>.
/// </summary>
public sealed class TestConnectionHandler(
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
    : ICommandHandler<TestConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(TestConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);

        var secrets = ConnectionSecrets.ReadForTest(protector, connection);
        var result = await tester.TestAsync(provider, connection.Fields, secrets, cancellationToken);
        var now = clock.UtcNow;
        if (result.Outcome == ConnectionTestOutcome.Ok)
        {
            connection.MarkVerified(now);
            if (result.RefreshedFields is { Count: > 0 } refreshed)
            {
                connection.ApplyProviderFields(provider, refreshed);
            }

            ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now);
        }
        else
        {
            ConnectionAudit.ByMember(
                auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now, ConnectionAuditActions.Failure);
            if (result.Outcome == ConnectionTestOutcome.CredentialsRejected && connection.Status == ConnectionStatus.Active)
            {
                // Un rechazo no es intermitente (D2): la conexión sale de los consumidores ya.
                ConnectionTransitions.FlagNeedsAttention(auditRecorder, eventPublisher, executionContext, connection, result, now);
            }
            else
            {
                connection.RecordFailure(ConnectionVerification.FailureCode(result), now);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
