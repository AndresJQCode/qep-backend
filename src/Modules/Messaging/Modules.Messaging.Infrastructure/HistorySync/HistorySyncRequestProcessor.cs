using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Coexistence;

/// <summary>
/// Spec 2026-10-29: le pide a Meta (<c>smb_app_data</c>) los contactos y el historial de cada importación con algún tipo
/// <c>Pending</c>, dentro de las 24 horas de la conexión. Singleton: el estado es del proceso.
/// </summary>
/// <remarks>
/// <para><b>Reclamo.</b> Una sentencia que se commitea sola: suma el intento y deja la fila reclamada 2 minutos, así otra réplica
/// no la toma y un pedido sin confirmar se repite cuando el reclamo vence. Una importación vencida, rechazada por la persona o ya
/// pedida no entra.</para>
/// <para><b>Orden (P-2).</b> Primero <c>smb_app_state_sync</c>, después <c>history</c>; cada tipo tiene su estado y uno que falla no
/// frena al otro. Cada resultado se guarda apenas llega: una caída entre los dos pedidos no repite el primero.</para>
/// <para><b>Resultado.</b> 2xx con <c>request_id</c> → <c>Requested</c>; error de Graph → <c>Failed</c> con <c>graph:{code}</c>
/// (o <c>graph:{status}</c> sin código); sin confirmar (timeout, red, 5xx) → sigue <c>Pending</c> con <c>unconfirmed</c>. Sin
/// remitente (conexión pausada, borrada o sin token) → sigue <c>Pending</c> con <c>connection_unavailable</c>, sin llamar a Meta.</para>
/// <para><b>Log.</b> Sólo la conexión, el tipo, el status y el código: nunca el token, el número ni el <c>request_id</c>.</para>
/// </remarks>
internal sealed partial class HistorySyncRequestProcessor(IServiceScopeFactory scopeFactory, ILogger<HistorySyncRequestProcessor> logger)
{
    internal const int BatchSize = 10;
    internal const string ConnectionUnavailable = "connection_unavailable";

    /// <summary>El orden de P-2.</summary>
    private static readonly HistorySyncType[] Types = [HistorySyncType.SmbAppStateSync, HistorySyncType.History];

    [LoggerMessage(Level = LogLevel.Information, Message = "History sync {ConnectionId}: {SyncType} requested.")]
    private static partial void LogRequested(ILogger logger, Guid connectionId, HistorySyncType syncType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History sync {ConnectionId}: {SyncType} rejected by Graph (HTTP {HttpStatus}, code {Code}, subcode {Subcode}).")]
    private static partial void LogRejected(ILogger logger, Guid connectionId, HistorySyncType syncType, int httpStatus, int? code, int? subcode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History sync {ConnectionId}: {SyncType} unconfirmed (HTTP {HttpStatus}); it is retried when its claim expires.")]
    private static partial void LogUnconfirmed(ILogger logger, Guid connectionId, HistorySyncType syncType, int httpStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History sync {ConnectionId}: the connection has no sender; it is retried when its claim expires.")]
    private static partial void LogConnectionUnavailable(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "History sync {ConnectionId} failed; it is retried when its claim expires.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid connectionId);

    public async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        List<ClaimedSync> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            claimed = await dbContext.Database.SqlQuery<ClaimedSync>(
                $"""
                UPDATE messaging.history_syncs SET claimed_until = {now} + interval '2 minutes', attempts = attempts + 1
                 WHERE connection_id IN (SELECT connection_id FROM messaging.history_syncs
                                          WHERE (contacts_state = 'Pending' OR history_state = 'Pending')
                                            AND connected_at + interval '24 hours' > {now}
                                            AND (claimed_until IS NULL OR claimed_until < {now})
                                            AND declined_at IS NULL
                                          ORDER BY connected_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
                RETURNING connection_id AS "ConnectionId", tenant_id AS "TenantId"
                """).ToListAsync(cancellationToken);
        }

        foreach (var sync in claimed)
        {
            try
            {
                await RequestAsync(sync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El intento quedó contado en el reclamo: la fila vuelve cuando venza.
                LogFailed(logger, exception, sync.ConnectionId);
            }
        }
    }

    private async Task RequestAsync(ClaimedSync sync, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<MessagingDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var row = await dbContext.HistorySyncs.AsNoTracking()
            .Where(value => value.ConnectionId == sync.ConnectionId)
            .Select(value => new { value.ContactsState, value.HistoryState })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return;
        }

        var pending = Types
            .Where(type => (type == HistorySyncType.SmbAppStateSync ? row.ContactsState : row.HistoryState) == HistorySyncRequestState.Pending)
            .ToList();
        var sender = await services.GetRequiredService<IMessagingConnectionDirectory>()
            .ResolveSenderAsync(sync.TenantId, sync.ConnectionId, cancellationToken);
        if (sender is null)
        {
            LogConnectionUnavailable(logger, sync.ConnectionId);
            foreach (var type in pending)
            {
                await SaveAsync(dbContext, sync.ConnectionId, type, HistorySyncRequestState.Pending, null, ConnectionUnavailable, clock.UtcNow, cancellationToken);
            }

            return;
        }

        var client = services.GetRequiredService<IWhatsAppCloudClient>();
        foreach (var type in pending)
        {
            var result = await client.RequestSmbAppDataAsync(sender, type, cancellationToken);
            var now = clock.UtcNow;
            if (result.Succeeded && result.Value is { } requestId)
            {
                LogRequested(logger, sync.ConnectionId, type);
                await SaveAsync(dbContext, sync.ConnectionId, type, HistorySyncRequestState.Requested, requestId, null, now, cancellationToken);
            }
            else if (result.Failure is { Reason: MessagingGraphFailure.UnconfirmedReason } unconfirmed)
            {
                LogUnconfirmed(logger, sync.ConnectionId, type, unconfirmed.HttpStatus);
                await SaveAsync(dbContext, sync.ConnectionId, type, HistorySyncRequestState.Pending, null, MessagingGraphFailure.UnconfirmedReason, now, cancellationToken);
            }
            else
            {
                var failure = result.Failure ?? new MessagingGraphFailure(0, null, "unknown");
                LogRejected(logger, sync.ConnectionId, type, failure.HttpStatus, failure.Code, failure.Subcode);
                var code = string.Create(CultureInfo.InvariantCulture, $"graph:{failure.Code ?? failure.HttpStatus}");
                await SaveAsync(dbContext, sync.ConnectionId, type, HistorySyncRequestState.Failed, null, code, now, cancellationToken);
            }
        }
    }

    /// <summary>Un tipo, en su propia sentencia (se commitea sola). Sólo toca un tipo que sigue <c>Pending</c>: si en el medio la persona
    /// o Meta cambiaron algo, lo de ellos gana. <c>version</c> sube para que un reintento concurrente desde la API vea el cambio.</summary>
    private static Task<int> SaveAsync(
        MessagingDbContext dbContext, Guid connectionId, HistorySyncType type, HistorySyncRequestState state, string? requestId, string? errorCode,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stateName = state.ToString();
        var requestedAt = state == HistorySyncRequestState.Requested ? now : (DateTimeOffset?)null;
        return type == HistorySyncType.SmbAppStateSync
            ? dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE messaging.history_syncs
                   SET contacts_state = {stateName}, contacts_request_id = {requestId}, contacts_requested_at = {requestedAt},
                       contacts_error_code = {errorCode}, updated_at = {now}, version = version + 1
                 WHERE connection_id = {connectionId} AND contacts_state = 'Pending'
                """,
                cancellationToken)
            : dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE messaging.history_syncs
                   SET history_state = {stateName}, history_request_id = {requestId}, history_requested_at = {requestedAt},
                       history_error_code = {errorCode}, updated_at = {now}, version = version + 1
                 WHERE connection_id = {connectionId} AND history_state = 'Pending'
                """,
                cancellationToken);
    }

    private sealed class ClaimedSync
    {
        public Guid ConnectionId { get; set; }

        public Guid TenantId { get; set; }
    }
}
