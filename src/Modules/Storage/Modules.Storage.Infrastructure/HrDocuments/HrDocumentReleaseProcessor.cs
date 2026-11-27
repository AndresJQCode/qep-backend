using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.Messaging;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.HrDocuments;

internal interface IHrDocumentReleaseProcessor
{
    /// <summary>Una pasada: reclama un lote de avisos y purga sus PDF. Devuelve cuántos terminó.</summary>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Consumidor»): consume <c>hr.employee.documents-released.v1</c> del outbox de plataforma,
/// calco de <c>CustomerMediaPurgeProcessor</c> de Messaging. Cada mensaje se reclama (<see cref="ClaimedOutboxConsumer"/>) y se procesa en
/// su propio scope con <see cref="HrDocumentPurge"/>. Una falla deja el mensaje sin marcar y vuelve cuando vence su reclamo: 5 s, 30 s y
/// 5 min, y desde ahí 5 min; nunca se abandona. Storage no referencia Hr: el nombre del evento y el payload (camelCase) son contrato y se
/// leen por nombre. Singleton: el estado es del proceso.
/// </summary>
internal sealed partial class HrDocumentReleaseProcessor(IServiceScopeFactory scopeFactory, ILogger<HrDocumentReleaseProcessor> logger)
    : IHrDocumentReleaseProcessor
{
    internal const string Consumer = "storage.hr-document-release";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    private readonly ClaimedOutboxConsumer claims = new(scopeFactory, Consumer, [StorageSubscribedEvents.HrDocumentsReleased], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage failed to purge HR documents for outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage keeps failing to purge HR documents for outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        await claims.ProcessBatchAsync(
            async (message, token) =>
            {
                await ProcessAsync(message, token);
                processed++;
            },
            LogFailure,
            cancellationToken);
        return processed;
    }

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var record = message.Record;
        if (!TryReadPayload(record.PayloadJson, out var tenantId, out var fileIds))
        {
            // Un payload roto es determinista: reintentarlo no lo arregla. Nada se borra.
            LogMalformed(logger, record.Id, record.EventName);
            await claims.MarkProcessedAsync(message, cancellationToken);
            await message.DbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var purge = message.Services.GetRequiredService<HrDocumentPurge>();
        await purge.PurgeAsync(tenantId, fileIds, () => claims.MarkProcessedAsync(message, cancellationToken), cancellationToken);
    }

    /// <summary>Malformado es lo que no deja saber de qué tenant es el aviso o cuáles son sus archivos: sin <c>tenantId</c> Guid no vacío, o
    /// sin <c>fileIds</c> arreglo de Guid. Un id suelto que no es Guid hace malformado el aviso entero: adivinar qué quiso decir el productor
    /// no es algo que se haga antes de un borrado permanente.</summary>
    internal static bool TryReadPayload(string json, out Guid tenantId, out IReadOnlyList<Guid> fileIds)
    {
        tenantId = Guid.Empty;
        fileIds = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tenantId", out var tenant)
                || tenant.ValueKind != JsonValueKind.String
                || !tenant.TryGetGuid(out var readTenant)
                || readTenant == Guid.Empty
                || !root.TryGetProperty("fileIds", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var read = new List<Guid>();
            foreach (var element in ids.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || !element.TryGetGuid(out var fileId))
                {
                    return false;
                }

                read.Add(fileId);
            }

            tenantId = readTenant;
            fileIds = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    internal void LogFailure(Exception exception, StorageOutboxMessage record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, record.EventName, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, record.EventName, attempt);
        }
    }
}
