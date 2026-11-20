using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Modules.Wholesale.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-11-18: consume del outbox de plataforma el aviso por WhatsApp de la invitación y lo manda con
/// <see cref="IWholesaleInvitationNoticeSender"/> (adaptador en Bootstrapper, D-1). Desde spec 2026-11-19 §2 el evento es
/// <c>wholesale.distributor-invitation-link.v1</c>, que trae el token, y el procesador le pasa al adaptador el link armado con
/// <see cref="IWholesaleInvitationLinks"/>; el viejo <c>wholesale.distributor-invitation-notice.v1</c> sigue consumiéndose,
/// sin link, para drenar los pendientes.
/// <para>Por mensaje: un payload malformado se marca procesado sin efecto; un aviso que ya no es vigente (distribuidor borrado,
/// aceptado o con otra <c>generation</c>, D-4) se marca procesado sin mandar. Si no, se manda con el id del mensaje como
/// <c>clientId</c> (D-5), se registra <c>Sent</c> o <c>Failed</c> con su código y el inbox se marca, todo en un solo
/// <c>SaveChanges</c>. Un fallo del envío es un resultado, no una excepción: no se reintenta.</para>
/// <para>Una excepción no marca nada: el mensaje vuelve cuando su reclamo venza (el aviso sigue <c>Pending</c>) y el reintento
/// usa el mismo <c>clientId</c>, así que una fila que ya salió no se manda dos veces. Los logs llevan sólo ids, nunca el
/// teléfono. Singleton: el estado es del proceso. La curva es la de Pipeline y Compras (5 s, 30 s, 5 minutos).</para>
/// </summary>
internal sealed partial class InvitationNoticeProcessor(IServiceScopeFactory scopeFactory, ILogger<InvitationNoticeProcessor> logger)
{
    internal const string Consumer = "wholesale.invitation-whatsapp-notice";

    /// <summary>El aviso viejo, sin link: ya no se escribe y se sigue consumiendo sólo para drenar los pendientes.</summary>
    internal const string NoticeEvent = WholesaleEventPublisher.InvitationNoticeEvent;

    /// <summary>Spec 2026-11-19 §2: el aviso con link. Mismo consumidor, mismo inbox y mismas reglas (D-3, D-4, D-5).</summary>
    internal const string LinkEvent = WholesaleEventPublisher.InvitationLinkEvent;

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } = new(scopeFactory, Consumer, [NoticeEvent, LinkEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invitation notice {MessageId} for distributor {DistributorId} is no longer current; it is marked as processed without sending.")]
    private static partial void LogStale(ILogger logger, Guid messageId, Guid distributorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invitation notice {MessageId} for distributor {DistributorId} failed with {FailureCode}.")]
    private static partial void LogNoticeFailed(ILogger logger, Guid messageId, Guid distributorId, string failureCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wholesale could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wholesale failed to deliver invitation notice {MessageId} on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wholesale keeps failing to deliver invitation notice {MessageId} on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, int attempt);

    public Task<bool> ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var unitOfWork = message.Services.GetRequiredService<IWholesaleUnitOfWork>();
        var notices = message.Services.GetRequiredService<DistributorInvitationNotices>();
        var record = message.Record;

        if (!TryRead(record.PayloadJson, record.EventName == LinkEvent, out var payload))
        {
            LogMalformed(logger, record.Id, record.EventName);
            await FinishAsync(message, unitOfWork, cancellationToken);
            return;
        }

        if (await notices.FindCurrentAsync(payload.TenantId, payload.DistributorId, payload.Generation, cancellationToken) is null)
        {
            LogStale(logger, record.Id, payload.DistributorId);
            await FinishAsync(message, unitOfWork, cancellationToken);
            return;
        }

        var sender = message.Services.GetRequiredService<IWholesaleInvitationNoticeSender>();
        var link = payload.Token is { } token
            ? message.Services.GetRequiredService<IWholesaleInvitationLinks>().Compose(token)
            : null;
        var result = await sender.SendAsync(
            new WholesaleInvitationNotice(
                record.Id, payload.TenantId, payload.ConnectionId, payload.SentByMemberId, payload.Phone, payload.ContactName,
                payload.Email, link),
            cancellationToken);
        var (status, failureCode) = result switch
        {
            WholesaleNoticeSendResult.Failed failed => (InvitationNoticeStatus.Failed, failed.Code),
            _ => (InvitationNoticeStatus.Sent, (string?)null),
        };
        if (failureCode is not null)
        {
            LogNoticeFailed(logger, record.Id, payload.DistributorId, failureCode);
        }

        // El envío pudo tardar: un PUT o un «Reenviar» pudo cambiar la generación mientras tanto. Se suelta lo leído antes y se
        // relee, así el resultado de una generación vieja no se aplica (D-4). El token de concurrencia de la generación cubre el
        // resto de la carrera, hasta el SaveChanges.
        unitOfWork.DiscardPendingChanges();
        await notices.RecordAsync(
            payload.TenantId, payload.DistributorId, payload.Generation, status, failureCode, cancellationToken);
        await FinishAsync(message, unitOfWork, cancellationToken);
    }

    private async Task FinishAsync(ClaimedMessage message, IWholesaleUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        await Claims.MarkProcessedAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>El payload que escribe <see cref="WholesaleEventPublisher"/>, con sus nombres en minúscula. Falta algo o sobra un
    /// tipo: malformado. Con <paramref name="withToken"/> (el aviso con link) el token es obligatorio; en el viejo no se lee.</summary>
    internal static bool TryRead(string json, bool withToken, out NoticePayload payload)
    {
        payload = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGuid(root, "tenantId", out var tenantId)
                || !TryGuid(root, "distributorId", out var distributorId)
                || !TryGuid(root, "connectionId", out var connectionId)
                || !TryGuid(root, "sentByMemberId", out var sentByMemberId)
                || !TryText(root, "phone", out var phone)
                || !TryText(root, "contactName", out var contactName)
                || !TryText(root, "email", out var email)
                || !root.TryGetProperty("generation", out var generationElement)
                || generationElement.ValueKind != JsonValueKind.Number
                || !generationElement.TryGetInt32(out var generation))
            {
                return false;
            }

            string? token = null;
            if (withToken)
            {
                if (!TryText(root, "token", out var text))
                {
                    return false;
                }

                token = text;
            }

            payload = new NoticePayload(
                tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, generation, token);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetGuid(out value)
            && value != Guid.Empty;
    }

    private static bool TryText(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, WholesaleOutboxMessage record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, attempt);
        }
    }

    /// <summary>Lo que el aviso necesita del evento. El teléfono y el token viajan sólo hasta el adaptador; nunca a un log.
    /// <c>Token</c> es <c>null</c> en el aviso viejo.</summary>
    internal sealed record NoticePayload(
        Guid TenantId,
        Guid DistributorId,
        string Phone,
        Guid ConnectionId,
        Guid SentByMemberId,
        string ContactName,
        string Email,
        int Generation,
        string? Token)
    {
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"TenantId = {TenantId}, DistributorId = {DistributorId}, Generation = {Generation}");
            return true;
        }
    }
}
