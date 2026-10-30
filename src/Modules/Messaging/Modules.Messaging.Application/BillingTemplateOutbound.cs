using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Cobros recurrentes §5: lo que Billing manda por la conexión de WhatsApp Business elegida. <c>WaId</c> son sólo dígitos, con indicativo.
/// <c>SentByMemberId</c> es el encargado del perfil de cobro (la fila exige un autor). <c>CustomerId</c> liga la conversación nueva al
/// cliente. <c>TemplateName</c> y <c>TemplateLanguage</c> salen de <see cref="IBillingTemplateReadiness"/>, nunca de la constante: tras un
/// reintento la vigente puede ser <c>qep_cobro_recordatorio_v2</c>. <c>PositionalParameters</c> ya vienen formateados, en el orden de
/// <c>{{1}}</c>…
/// </summary>
public sealed record BillingTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    MessageTemplateKind Kind,
    string TemplateName, string TemplateLanguage,
    IReadOnlyList<string> PositionalParameters);

public abstract record BillingTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : BillingTemplateSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>secret_unreadable</c>,
    /// <c>credentials_rejected</c>, …), como en el envío del bot.</summary>
    public sealed record ConnectionUnavailable(string? Reason) : BillingTemplateSendResult;

    public sealed record Failed(string Code) : BillingTemplateSendResult;
}

public interface IBillingTemplateOutbound
{
    Task<BillingTemplateSendResult> SendAsync(BillingTemplateSend send, CancellationToken cancellationToken);
}

/// <summary>
/// Cobros recurrentes §5: la plantilla de cobro, posicional y sin encabezado de documento, al hilo del número en esa conexión. Es
/// <see cref="QuotationTemplateOutbound"/> sin documento ni R2 y por la misma razón no usa <see cref="OutboundGuards"/>: el envío lo autoriza
/// Billing, no se rechaza si la conversación está resuelta o asignada a otra persona y no cambia su estado ni su asignación. Tampoco resta el
/// no leído (<c>seen: 0</c>): nadie leyó el hilo para enviar. Una falla tardía (Meta rechazando la credencial al enviar) puede dejar una
/// conversación nueva vacía, como en <see cref="StartConversationHandler"/>: la conversación se abre antes del reclamo.
/// </summary>
public sealed class BillingTemplateOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener) : IBillingTemplateOutbound
{
    public async Task<BillingTemplateSendResult> SendAsync(BillingTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var definition = BillingTemplateDefinitions.For(send.Kind);

        // 1. Las reglas de Meta sobre cada valor (error 132018), antes de tocar nada.
        if (!IsAcceptable(send, definition))
        {
            return new BillingTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected);
        }

        // 2.
        var resolution = await connections.ResolveSenderWithReasonAsync(send.TenantId, send.ConnectionId, cancellationToken);
        if (resolution.Sender is not { } sender)
        {
            return new BillingTemplateSendResult.ConnectionUnavailable(resolution.UnavailableReason);
        }

        // 3. Por el teléfono de destino, sin asignar a nadie.
        var now = clock.UtcNow;
        var opened = await opener.OpenByPhoneAsync(send.TenantId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, now, cancellationToken);
        var conversation = await conversations.FindAsync(send.TenantId, opened.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation that was just opened could not be read.");

        // 4. Un clientId nuevo: cada aviso es un mensaje propio, no un reintento.
        var parameters = definition.ParametersFor(send.PositionalParameters);
        var values = parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.Ordinal);
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, send.ConnectionId, Guid.CreateVersion7(), definition.Render(parameters),
            send.SentByMemberId, now, Kind: MessageKind.Template,
            DetailsJson: TemplateDetailsJson.Serialize(send.TemplateName, send.TemplateLanguage, BillingTemplateDefinitions.Category, values));
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // 5 y 6. Desde acá todo va con CancellationToken.None, como en el envío del asesor: si Meta ya aceptó, cancelar no puede borrar la fila.
        try
        {
            var result = await meta.SendTemplateAsync(
                sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId),
                new TemplateSend(send.TemplateName, send.TemplateLanguage, Named: false, parameters),
                SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
                CancellationToken.None);
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(send.TenantId, send.ConnectionId, claim, result, clock.UtcNow, metaMediaId: null, afterRollback: null, seen: 0);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            // La liquidación sólo tira este código cuando Meta rechazó la credencial o el número; la conexión ya quedó NeedsAttention.
            return new BillingTemplateSendResult.ConnectionUnavailable(BotOutbound.CredentialsRejected);
        }
        catch (MessagingDomainException exception)
        {
            // Failed: la fila queda, para que el hilo muestre qué no salió.
            return new BillingTemplateSendResult.Failed(exception.Code);
        }

        return new BillingTemplateSendResult.Sent(claim.MessageId, conversation.Id);
    }

    private static bool IsAcceptable(BillingTemplateSend send, BillingTemplateDefinition definition) =>
        // El teléfono es la clave de la conversación: con otra forma, el opener no podría usarlo.
        send.WaId is { Length: > 0 and <= Conversation.WaIdMaxLength } waId && waId.All(char.IsAsciiDigit)
        && send.PositionalParameters.Count == definition.Examples.Count
        && send.PositionalParameters.All(TemplateParameterRules.IsValid);
}
