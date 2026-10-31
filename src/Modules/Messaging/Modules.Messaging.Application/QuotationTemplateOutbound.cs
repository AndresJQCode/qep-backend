using System.Security.Cryptography;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-21 D16: lo que Quotations manda por la conexión de WhatsApp Business elegida. <c>WaId</c> son sólo dígitos, con
/// indicativo. <c>CustomerId</c> es <c>null</c> con destinatario de facturación (D16b). <c>RecipientName</c> es el <c>profile_name</c> de
/// una conversación nueva. <c>Template</c> es la fila aprobada que devuelve <c>FindApprovedAsync</c>, nunca la constante: tras un reintento
/// la vigente puede ser <c>qep_cotizacion_v2</c>, y su cuerpo puede ser el que editó el tenant. Los cuatro valores ya vienen formateados;
/// sólo viajan los que la fila usa.
/// </summary>
public sealed record QuotationTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    ApprovedTemplate Template,
    string FullName, string OrderNumber, string Total, string ValidUntil,
    ReadOnlyMemory<byte> Document, string DocumentFileName);

public abstract record QuotationTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : QuotationTemplateSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>secret_unreadable</c>,
    /// <c>credentials_rejected</c>, …), como en el envío del bot.</summary>
    public sealed record ConnectionUnavailable(string? Reason) : QuotationTemplateSendResult;

    public sealed record Failed(string Code) : QuotationTemplateSendResult;
}

public interface IQuotationTemplateOutbound
{
    Task<QuotationTemplateSendResult> SendAsync(QuotationTemplateSend send, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-21 D16, D16a, D16b y D16c: la plantilla de cotización, con el PDF como encabezado de documento, al hilo del número en
/// esa conexión. No usa <see cref="OutboundGuards"/> a propósito: esas son las guardas de la bandeja (membresía y permiso de Mensajería
/// de quien envía, conversación <c>Open</c>, enviar es tomarla), y D16a pide lo contrario: el envío lo autoriza
/// <c>quotations.quotation.manage</c> en Quotations, no se rechaza si la conversación está resuelta o asignada a otra persona y no cambia
/// su estado ni su asignación. Tampoco resta el no leído (<c>seen: 0</c>): nadie leyó el hilo para enviar.
/// <para>
/// Comparte con el asesor lo que no es guarda: la subida a Meta antes de reclamar (como <see cref="SendMediaMessageHandler"/>), el
/// reclamo con el medio y la tabla de respuestas (<see cref="OutboundSettlement"/>). El PDF queda en el R2 de Mensajería con la clave del
/// mensaje (D16c), no como la URL pública de Quotations, que el lifecycle del bucket borra.
/// </para>
/// <para>
/// Una falla tardía (la escritura en R2, o Meta rechazando la credencial al enviar) puede dejar una conversación nueva vacía, como
/// en <see cref="StartConversationHandler"/>: la conversación se abre antes del reclamo.
/// </para>
/// </summary>
public sealed class QuotationTemplateOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    IMessagingMediaStore store,
    TemplateSenderValues senderValues) : IQuotationTemplateOutbound
{
    // Las tres claves propias de la cotización; las del remitente están en SystemTemplateVariables.
    private const string QuotationNumberKey = "numero_cotizacion";
    private const string TotalKey = "valor_total";
    private const string ValidUntilKey = "vigencia";

    private const string PdfMimeType = "application/pdf";

    // El multipart lleva un nombre genérico ASCII; el que ve el cliente viaja en el encabezado de la plantilla.
    private const string GenericDocumentName = "document.pdf";

    public async Task<QuotationTemplateSendResult> SendAsync(QuotationTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        // 1. El teléfono, y después las reglas de Meta sobre cada valor que la fila usa (error 132018), antes de tocar nada. Un valor que la
        // fila no usa no la bloquea: no viaja.
        if (!IsAcceptable(send))
        {
            return new QuotationTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SystemTemplateVariables.ContactName] = send.FullName,
            [QuotationNumberKey] = send.OrderNumber,
            [TotalKey] = send.Total,
            [ValidUntilKey] = send.ValidUntil,
        };
        await senderValues.AddAsync(send.TenantId, send.SentByMemberId, SystemTemplateSend.VariablesOf(send.Template), values, cancellationToken);
        if (SystemTemplateSend.Build(send.Template, values) is not { } built)
        {
            return new QuotationTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected);
        }

        // 2.
        var resolution = await connections.ResolveSenderWithReasonAsync(send.TenantId, send.ConnectionId, cancellationToken);
        if (resolution.Sender is not { } sender)
        {
            return new QuotationTemplateSendResult.ConnectionUnavailable(resolution.UnavailableReason);
        }

        // 3. La subida a Meta va antes del reclamo, como el adjunto del asesor: si falla no queda fila ni conversación.
        var document = send.Document.ToArray();
        MessagingGraphResult<string> upload;
        using (var content = new MemoryStream(document, writable: false))
        {
            upload = await meta.UploadMediaAsync(sender, content, document.Length, PdfMimeType, GenericDocumentName, cancellationToken);
        }

        if (upload is not { Succeeded: true, Value: { Length: > 0 } mediaId })
        {
            // La misma regla que la subida del adjunto del asesor (FromUploadFailure → OutboundSettlement): un 4xx con 190/133010
            // dice que la credencial o el número ya no sirven, y la conexión pasa a NeedsAttention. Cualquier otra falla es de la subida.
            if (upload.Failure is { HttpStatus: >= 400 and < 500, Code: MetaErrorCodes.TokenExpired or MetaErrorCodes.NumberUnregistered } rejected)
            {
                await ReportRejectedAsync(send.TenantId, send.ConnectionId, MetaErrorCodes.RejectedReason(rejected.Code!.Value));
                return new QuotationTemplateSendResult.ConnectionUnavailable(BotOutbound.CredentialsRejected);
            }

            return new QuotationTemplateSendResult.Failed(MessagingErrorCodes.MediaUploadFailed);
        }

        // 4. D16b: por el teléfono de destino, sin asignar a nadie.
        var now = clock.UtcNow;
        var opened = await opener.OpenByPhoneAsync(send.TenantId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, now, cancellationToken);
        var conversation = await conversations.FindAsync(send.TenantId, opened.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation that was just opened could not be read.");

        // 5. Un clientId nuevo: cada envío de la cotización es un mensaje propio, no un reintento.
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, send.ConnectionId, Guid.CreateVersion7(), built.Text,
            send.SentByMemberId, now, Kind: MessageKind.Template,
            Media: new OutboundMedia(PdfMimeType, document.Length, Convert.ToHexStringLower(SHA256.HashData(document)), send.DocumentFileName),
            DetailsJson: built.DetailsJson);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // 6. D16c: la copia propia de Mensajería, con la clave del reclamo.
        var key = MessagingMediaKeys.For(send.TenantId, claim.MessageId);
        try
        {
            using var content = new MemoryStream(document, writable: false);
            await store.UploadAsync(key, content, document.Length, PdfMimeType, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No se envió nada y la fila no queda.
            await claim.RollbackAsync(CancellationToken.None);
            return new QuotationTemplateSendResult.Failed(MessagingErrorCodes.MediaUploadFailed);
        }

        // 7 y 8. Desde acá todo va con CancellationToken.None, como en el envío del asesor: si Meta ya aceptó, cancelar no puede borrar
        // la fila. Un rollback por credencial deja sin fila el objeto que se acaba de escribir: se borra de R2.
        try
        {
            var result = await meta.SendTemplateAsync(
                sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId),
                built.Send with { HeaderDocument = new TemplateHeaderDocument(mediaId, send.DocumentFileName) },
                SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
                CancellationToken.None);
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(
                    send.TenantId, send.ConnectionId, claim, result, clock.UtcNow, metaMediaId: mediaId,
                    afterRollback: claim.Inserted ? () => DiscardAsync(key) : null, seen: 0);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            // La liquidación sólo tira este código cuando Meta rechazó la credencial o el número; la conexión ya quedó NeedsAttention.
            return new QuotationTemplateSendResult.ConnectionUnavailable(BotOutbound.CredentialsRejected);
        }
        catch (MessagingDomainException exception)
        {
            // Failed: la fila y su documento quedan, para que el hilo muestre qué no salió.
            return new QuotationTemplateSendResult.Failed(exception.Code);
        }
        catch (Exception) when (claim.Inserted)
        {
            await DiscardAsync(key);
            throw;
        }

        return new QuotationTemplateSendResult.Sent(claim.MessageId, conversation.Id);
    }

    private static bool IsAcceptable(QuotationTemplateSend send) =>
        // El teléfono es la clave de la conversación: con otra forma, el opener no podría usarlo.
        send.WaId is { Length: > 0 and <= Conversation.WaIdMaxLength } waId && waId.All(char.IsAsciiDigit);

    // Best effort: la respuesta ya es connection_unavailable, y el próximo envío vuelve a recibir 190/133010 y reintenta el reporte.
    // Application no tiene logger; no vale cambiar la respuesta por eso.
    private async Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode)
    {
        try
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, failureCode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }

    // Best effort, como en el adjunto del asesor: si borrar falla queda un objeto que ninguna fila sirve, y no vale cambiar la respuesta.
    private async Task DiscardAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }
}
