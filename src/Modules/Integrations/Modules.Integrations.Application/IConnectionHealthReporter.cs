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

    /// <summary>
    /// Spec 2026-11-12 §6.3 (D9): el proveedor ya no deja renovar el token. Pasa a <c>NeedsAttention</c> con
    /// <c>token_expired</c> desde <c>Active</c>, <c>Paused</c> o <c>NeedsAttention</c> con otro código, porque nada fuera de
    /// una nueva autorización la recupera. Audita como <see cref="ReportCredentialsRejectedAsync"/>; publica el evento, con
    /// <c>token_expired</c> en el payload, si estaba <c>Active</c> o en <c>NeedsAttention</c> con otro código (owner,
    /// 2026-11-12: el evento identifica el error), y no desde <c>Paused</c>. Ya en <c>token_expired</c> o
    /// <c>app_uninstalled</c> no hace nada.
    /// </summary>
    Task ReportTokenExpiredAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
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

        await MarkAsync(connection, failureCode, publish: true, cancellationToken);
    }

    public async Task ReportTokenExpiredAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is null || ShopifyTokenRefresh.IsUnrecoverable(connection.Status, connection.LastFailureCode))
        {
            return;
        }

        // Owner, 2026-11-12: el evento identifica el error, así que una en atención por otra causa publica otra vez, con
        // token_expired. Una pausada no: la pausa fue del usuario. La que ya está en token_expired salió arriba.
        var publish = connection.Status is ConnectionStatus.Active or ConnectionStatus.NeedsAttention;
        await MarkAsync(connection, ConnectionFailureCodes.TokenExpired, publish, cancellationToken);
    }

    private async Task MarkAsync(
        IntegrationConnection connection, string failureCode, bool publish, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        connection.MarkNeedsAttention(failureCode, now);
        auditRecorder.Record(
            connection.TenantId,
            connection.Id,
            AuditActorType.Integration,
            ConnectionAuditActions.NeedsAttention,
            connection.Id,
            ConnectionAuditActions.Success,
            [],
            now);
        if (publish)
        {
            eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
