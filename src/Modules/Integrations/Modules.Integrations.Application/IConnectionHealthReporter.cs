using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08: un consumidor la llama tras un 401/403 <b>definitivo</b> del proveedor. Sin umbral
/// (D2): un rechazo de credenciales no es intermitente.
/// </summary>
public interface IConnectionHealthReporter
{
    Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);
}

/// <summary>
/// <c>Active → NeedsAttention</c>, audita y publica en la misma transacción. Lo que no está Active se
/// ignora: reportar dos veces no duplica nada (P15). No hay una persona detrás: el actor es la propia
/// conexión, con tipo <see cref="AuditActorType.Integration"/>.
/// </summary>
public sealed class ConnectionHealthReporter(
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    IClock clock) : IConnectionHealthReporter
{
    public async Task ReportCredentialsRejectedAsync(
        Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > IntegrationConnection.FailureCodeMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode), $"A failure code is at most {IntegrationConnection.FailureCodeMaxLength} characters.");
        }

        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active })
        {
            return;
        }

        var now = clock.UtcNow;
        connection.MarkNeedsAttention(failureCode, now);
        auditRecorder.Record(
            tenantId,
            connection.Id,
            AuditActorType.Integration,
            ConnectionAuditActions.NeedsAttention,
            connection.Id,
            ConnectionAuditActions.Success,
            [],
            now);
        eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
