using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Coexistence;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.CustomerDeletion;

internal interface ICustomerMediaPurgeProcessor
{
    /// <summary>Una pasada: reclama un lote de avisos y borra sus objetos. Devuelve cuántos terminó.</summary>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-10 (borrar clientes) A-9: consume <c>customers.customer.media-released.v1</c> del outbox de plataforma y borra de R2 las
/// claves que el cliente borrado dejó sin dueño. El borrado del cliente ya confirmó, así que una falla de R2 no deshace nada: el mensaje
/// queda sin marcar y vuelve cuando vence su reclamo, hasta que R2 responda. Borrar una clave que ya no existe no es un error (el
/// borrado de R2 es idempotente), así que reintentar un aviso a medias es seguro. Messaging no referencia Customers: el nombre del evento
/// y el payload (camelCase) son contrato y se leen por nombre. Singleton: el estado es del proceso.
/// </summary>
internal sealed partial class CustomerMediaPurgeProcessor(IServiceScopeFactory scopeFactory, ILogger<CustomerMediaPurgeProcessor> logger)
    : ICustomerMediaPurgeProcessor
{
    internal const string Consumer = "messaging.customer-media-purge";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    private readonly ClaimedOutboxConsumer claims = new(scopeFactory, Consumer, [MessagingSubscribedEvents.CustomerMediaReleased], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} carries storage key '{StorageKey}' outside its tenant's media prefixes; it is skipped.")]
    private static partial void LogKeySkipped(ILogger logger, Guid messageId, string storageKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging failed to purge media for outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging keeps failing to purge media for outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
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
        if (TryReadKeys(record.PayloadJson, out var tenantId, out var storageKeys))
        {
            var store = message.Services.GetRequiredService<IMessagingMediaStore>();
            foreach (var key in storageKeys)
            {
                // El borrado en R2 es permanente: sólo se borra lo que vive bajo el prefijo del tenant del aviso. Un productor con un
                // error podría mandar la clave de otro tenant, o una vacía; se salta esa clave y no el aviso entero.
                if (!BelongsToTenant(key, tenantId))
                {
                    LogKeySkipped(logger, record.Id, key);
                    continue;
                }

                // Si una clave falla, la excepción deja el mensaje sin marcar: el reintento vuelve a pasar por las que ya se borraron.
                await store.DeleteAsync(key, cancellationToken);
            }
        }
        else
        {
            // Un payload roto es determinista: reintentarlo no lo arregla.
            LogMalformed(logger, record.Id, record.EventName);
        }

        await claims.MarkProcessedAsync(message, cancellationToken);
        await MessagingSaveChanges.SaveAsync(message.DbContext, cancellationToken);
    }

    // Los dos prefijos de MessagingMediaKeys que guardan medios de un mensaje (For y Scheduled). El staging no entra: lo que queda
    // ahí no es de un mensaje y su ciclo de vida lo maneja R2.
    private static bool BelongsToTenant(string key, Guid tenantId) =>
        key.StartsWith($"messaging/{tenantId}/", StringComparison.Ordinal)
        || key.StartsWith($"messaging/scheduled/{tenantId}/", StringComparison.Ordinal);

    // Malformado es lo que no deja saber de qué tenant es el aviso ni cuáles son sus claves. Una clave suelta inválida (vacía, no
    // texto) no lo es: queda como "" y BelongsToTenant la descarta.
    private static bool TryReadKeys(string json, out Guid tenantId, out IReadOnlyList<string> storageKeys)
    {
        tenantId = Guid.Empty;
        storageKeys = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.GetProperty("tenantId").TryGetGuid(out tenantId) || tenantId == Guid.Empty)
            {
                return false;
            }

            var keys = new List<string>();
            foreach (var element in document.RootElement.GetProperty("storageKeys").EnumerateArray())
            {
                keys.Add(element.ValueKind == JsonValueKind.String ? element.GetString()! : "");
            }

            storageKeys = keys;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, MessagingOutboxRecord record, int? attempts)
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
