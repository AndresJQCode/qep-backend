using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Media;

internal sealed record PendingMedia(Guid MessageId, Guid TenantId, Guid ConnectionId, string MetaMediaId, DateTimeOffset OccurredAt, int Attempts);

/// <summary>§8.6, «Copia»: corre la transferencia (<see cref="MediaTransfer"/>) y deja el resultado en la
/// fila. Un fallo deja last_error y el reintento ya quedó programado por el reclamo; rendirse corre
/// next_attempt_at a <see cref="GiveUpAt"/>. Nunca registra el token ni la URL firmada.</summary>
internal sealed partial class MediaCopyProcessor(
    MessagingDbContext dbContext,
    MediaTransfer transfer,
    IMessagingMediaStore store,
    BuildingBlocks.Application.IClock clock,
    ILogger<MediaCopyProcessor> logger)
{
    public const int LastErrorMaxLength = 256;

    /// <summary>Rendirse = que el reclamo no la vuelva a tomar: el motivo queda en last_error.</summary>
    public static readonly DateTimeOffset GiveUpAt = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media of message {MessageId} could not be copied (attempt {Attempt}, giving up: {GiveUp}): {Reason}.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt, bool giveUp, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media of message {MessageId} was stored without verification: Meta's sha256 reference could not be decoded.")]
    private static partial void LogUnverified(ILogger logger, Guid messageId);

    public async Task CopyOneAsync(PendingMedia pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var result = await transfer.RunAsync(pending, cancellationToken);
        switch (result.Status)
        {
            case MediaTransferStatus.Stored:
            case MediaTransferStatus.StoredUnverified:
                if (result.Status == MediaTransferStatus.StoredUnverified)
                {
                    // Una sola vez por fila: queda guardada y no se vuelve a reclamar.
                    LogUnverified(logger, pending.MessageId);
                }

                await dbContext.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE messaging.message_media
                       SET stored_at = {clock.UtcNow}, storage_key = {result.Key}, size_bytes = {result.Length}, sha256 = {result.Sha256Hex},
                           mime_type = {result.MimeType}, last_error = NULL
                     WHERE message_id = {pending.MessageId}
                    """, cancellationToken);
                return;

            case MediaTransferStatus.Mismatch:
                await FailAsync(pending, await DiscardMismatchAsync(pending, result, cancellationToken), giveUp: false, cancellationToken);
                return;

            case MediaTransferStatus.GiveUp:
                await FailAsync(pending, result.Reason!, giveUp: true, cancellationToken);
                return;

            default:
                await FailAsync(pending, result.Reason!, giveUp: false, cancellationToken);
                return;
        }
    }

    /// <summary>Borra lo que se subió con largo o hash equivocados, salvo que otra réplica ya haya dado la fila
    /// por copiada (re-toma tras un lease vencido). Un fallo al borrar se suma al motivo, no se pierde.</summary>
    private async Task<string> DiscardMismatchAsync(PendingMedia pending, MediaTransferResult result, CancellationToken cancellationToken)
    {
        try
        {
            var stillPending = await dbContext.Media.AsNoTracking()
                .AnyAsync(media => media.MessageId == pending.MessageId && media.StoredAt == null, cancellationToken);
            if (stillPending)
            {
                await store.DeleteAsync(result.Key, cancellationToken);
            }

            return result.Reason!;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return result.Reason + ";delete:" + exception.GetType().Name;
        }
    }

    private async Task FailAsync(PendingMedia pending, string reason, bool giveUp, CancellationToken cancellationToken)
    {
        LogFailed(logger, pending.MessageId, pending.Attempts, giveUp, reason);
        var lastError = reason.Length <= LastErrorMaxLength ? reason : reason[..LastErrorMaxLength];
        if (giveUp)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE messaging.message_media SET last_error = {lastError}, next_attempt_at = {GiveUpAt} WHERE message_id = {pending.MessageId}",
                cancellationToken);
            return;
        }

        // El reintento ya lo programó el reclamo (next_attempt_at = now + lease).
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.message_media SET last_error = {lastError} WHERE message_id = {pending.MessageId}",
            cancellationToken);
    }
}
