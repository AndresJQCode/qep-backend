using System.Data.Common;
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
/// <para><b>Reclamo.</b> Una fila por sentencia, que se commitea sola (R7, como P2-R24 de Automations): suma el intento y la deja
/// reclamada 2 minutos, así otra réplica no la toma y un pedido sin confirmar se repite cuando el reclamo vence. Cada fila arranca con
/// el reclamo entero; una pasada atiende hasta <see cref="BatchSize"/>. Una importación vencida, rechazada por la persona o ya pedida
/// no entra. Antes de cada pedido se comprueba que el reclamo siga siendo de esta pasada, que no haya vencido y que el tipo siga
/// <c>Pending</c>: un reclamo viejo no pide. Una respuesta confirmada de Meta se guarda aunque el reclamo ya no sea propio (si no, se
/// volvería a pedir); lo que no confirma nada sólo con el reclamo propio. Meta acepta un solo pedido por tipo.</para>
/// <para><b>Orden (P-2).</b> Primero <c>smb_app_state_sync</c>, después <c>history</c>; cada tipo tiene su estado y uno que falla no
/// frena al otro. Cada resultado se guarda apenas llega, sin el token del apagado (lo que Meta ya contestó no se pierde por un
/// apagado) y con un reintento ante una falla transitoria de la base: una caída entre los dos pedidos no repite el primero.</para>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "History sync {ConnectionId}: its claim expired or was taken by another pass; the rest waits for the next claim.")]
    private static partial void LogClaimLost(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "History sync {ConnectionId} failed; it is retried when its claim expires.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid connectionId);

    public async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < BatchSize; processed++)
        {
            var sync = await ClaimOneAsync(cancellationToken);
            if (sync is null)
            {
                return;
            }

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

    /// <summary>Una fila por sentencia: el plan reclamaba hasta 10 de una vez, y la décima arrancaba con el reclamo ya gastado por las
    /// nueve anteriores (R7).</summary>
    private async Task<ClaimedSync?> ClaimOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var claimed = await dbContext.Database.SqlQuery<ClaimedSync>(
            $"""
            UPDATE messaging.history_syncs SET claimed_until = {now} + interval '2 minutes', attempts = attempts + 1
             WHERE connection_id IN (SELECT connection_id FROM messaging.history_syncs
                                      WHERE (contacts_state = 'Pending' OR history_state = 'Pending')
                                        AND connected_at + interval '24 hours' > {now}
                                        AND (claimed_until IS NULL OR claimed_until < {now})
                                        AND declined_at IS NULL
                                      ORDER BY connected_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING connection_id AS "ConnectionId", tenant_id AS "TenantId", claimed_until AS "ClaimedUntil"
            """).ToListAsync(cancellationToken);
        return claimed.Count == 0 ? null : claimed[0];
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
                await SaveAsync(dbContext, sync, type, HistorySyncRequestState.Pending, null, ConnectionUnavailable, clock.UtcNow, requireClaim: true);
            }

            return;
        }

        var client = services.GetRequiredService<IWhatsAppCloudClient>();
        foreach (var type in pending)
        {
            var check = await CheckBeforeSendAsync(dbContext, sync, type, clock.UtcNow, cancellationToken);
            if (check == SendCheck.ClaimLost)
            {
                LogClaimLost(logger, sync.ConnectionId);
                return;
            }

            if (check == SendCheck.NoLongerPending)
            {
                // Otra pasada ya lo pidió (o lo resolvió) después de que esta leyó la fila: Meta acepta un pedido por tipo.
                continue;
            }

            var result = await client.RequestSmbAppDataAsync(sender, type, cancellationToken);
            // Desde acá Meta ya contestó: se guarda sin el token del apagado.
            var now = clock.UtcNow;
            if (result.Succeeded && result.Value is { } requestId)
            {
                LogRequested(logger, sync.ConnectionId, type);
                // El id sólo se guarda, no se usa para correlacionar: uno más largo que la columna no puede dejar el tipo Pending (se
                // volvería a pedir, y Meta acepta un pedido por tipo).
                var stored = requestId.Length <= HistorySync.RequestIdMaxLength ? requestId : null;
                await SaveAsync(dbContext, sync, type, HistorySyncRequestState.Requested, stored, null, now, requireClaim: false);
            }
            else if (result.Failure is { Reason: MessagingGraphFailure.UnconfirmedReason } unconfirmed)
            {
                LogUnconfirmed(logger, sync.ConnectionId, type, unconfirmed.HttpStatus);
                if (!await SaveAsync(
                        dbContext, sync, type, HistorySyncRequestState.Pending, null, MessagingGraphFailure.UnconfirmedReason, now, requireClaim: true))
                {
                    LogClaimLost(logger, sync.ConnectionId);
                    return;
                }
            }
            else
            {
                var failure = result.Failure ?? new MessagingGraphFailure(0, null, "unknown");
                LogRejected(logger, sync.ConnectionId, type, failure.HttpStatus, failure.Code, failure.Subcode);
                var code = string.Create(CultureInfo.InvariantCulture, $"graph:{failure.Code ?? failure.HttpStatus}");
                await SaveAsync(dbContext, sync, type, HistorySyncRequestState.Failed, null, code, now, requireClaim: false);
            }
        }
    }

    private enum SendCheck
    {
        Send,
        ClaimLost,
        NoLongerPending,
    }

    /// <summary>Justo antes de pedir un tipo: el reclamo sigue siendo el de esta pasada y no venció (si no, otra pasada puede estar
    /// pidiendo lo mismo), y el tipo sigue <c>Pending</c> (la lista se armó al leer la fila, y otra pasada pudo pedirlo después).</summary>
    private static async Task<SendCheck> CheckBeforeSendAsync(
        MessagingDbContext dbContext, ClaimedSync sync, HistorySyncType type, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await dbContext.HistorySyncs.AsNoTracking()
            .Where(value => value.ConnectionId == sync.ConnectionId)
            .Select(value => new
            {
                ClaimedUntil = EF.Property<DateTimeOffset?>(value, "ClaimedUntil"),
                value.ContactsState,
                value.HistoryState,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null || current.ClaimedUntil != sync.ClaimedUntil || current.ClaimedUntil <= now)
        {
            return SendCheck.ClaimLost;
        }

        var state = type == HistorySyncType.SmbAppStateSync ? current.ContactsState : current.HistoryState;
        return state == HistorySyncRequestState.Pending ? SendCheck.Send : SendCheck.NoLongerPending;
    }

    /// <summary>
    /// Un tipo, en su propia sentencia (se commitea sola), sin token de cancelación: lo que Meta contestó se guarda aunque el host se
    /// esté apagando. Siempre exige que el tipo siga <c>Pending</c> (si la persona, Meta u otra pasada lo resolvieron, lo de ellos gana).
    /// <paramref name="requireClaim"/> separa los dos casos:
    /// <list type="bullet">
    /// <item>Una respuesta confirmada de Meta (aceptado o rechazado) se guarda aunque el reclamo haya vencido o lo tenga otra pasada:
    /// si no, el tipo quedaría <c>Pending</c> y la otra pasada lo pediría otra vez, gastando el único pedido.</item>
    /// <item>Lo que no confirma nada (<c>unconfirmed</c>, <c>connection_unavailable</c>) sólo se guarda con el reclamo propio: una
    /// pasada vieja no pisa el código del reclamo nuevo.</item>
    /// </list>
    /// <c>version</c> sube para que un reintento concurrente desde la API vea el cambio. Una falla transitoria de la base se reintenta
    /// una vez; si ese reintento no toca filas porque el primero sí llegó a commitear, el estado ya escrito cuenta como guardado.
    /// Devuelve si el estado quedó escrito.
    /// </summary>
    private static async Task<bool> SaveAsync(
        MessagingDbContext dbContext, ClaimedSync sync, HistorySyncType type, HistorySyncRequestState state, string? requestId, string? errorCode,
        DateTimeOffset now, bool requireClaim)
    {
        try
        {
            return await UpdateAsync(dbContext, sync, type, state, requestId, errorCode, now, requireClaim) > 0;
        }
        catch (DbException exception) when (exception.IsTransient)
        {
            if (await UpdateAsync(dbContext, sync, type, state, requestId, errorCode, now, requireClaim) > 0)
            {
                return true;
            }

            var written = await dbContext.HistorySyncs.AsNoTracking()
                .Where(value => value.ConnectionId == sync.ConnectionId)
                .Select(value => new { value.ContactsState, value.HistoryState, value.ContactsErrorCode, value.HistoryErrorCode })
                .SingleOrDefaultAsync(CancellationToken.None);
            return written is not null
                && (type == HistorySyncType.SmbAppStateSync
                    ? written.ContactsState == state && written.ContactsErrorCode == errorCode
                    : written.HistoryState == state && written.HistoryErrorCode == errorCode);
        }
    }

    private static Task<int> UpdateAsync(
        MessagingDbContext dbContext, ClaimedSync sync, HistorySyncType type, HistorySyncRequestState state, string? requestId, string? errorCode,
        DateTimeOffset now, bool requireClaim)
    {
        var stateName = state.ToString();
        var requestedAt = state == HistorySyncRequestState.Requested ? now : (DateTimeOffset?)null;
        return type == HistorySyncType.SmbAppStateSync
            ? dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE messaging.history_syncs
                   SET contacts_state = {stateName}, contacts_request_id = {requestId}, contacts_requested_at = {requestedAt},
                       contacts_error_code = {errorCode}, updated_at = {now}, version = version + 1
                 WHERE connection_id = {sync.ConnectionId} AND contacts_state = 'Pending'
                   AND ({requireClaim} = false OR claimed_until = {sync.ClaimedUntil})
                """,
                CancellationToken.None)
            : dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE messaging.history_syncs
                   SET history_state = {stateName}, history_request_id = {requestId}, history_requested_at = {requestedAt},
                       history_error_code = {errorCode}, updated_at = {now}, version = version + 1
                 WHERE connection_id = {sync.ConnectionId} AND history_state = 'Pending'
                   AND ({requireClaim} = false OR claimed_until = {sync.ClaimedUntil})
                """,
                CancellationToken.None);
    }

    private sealed class ClaimedSync
    {
        public Guid ConnectionId { get; set; }

        public Guid TenantId { get; set; }

        public DateTimeOffset ClaimedUntil { get; set; }
    }
}
