using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Domain.Engine;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-26 D-5, D-7 y D-10: el bloque «Política de datos» sobre el registro de Customers. Automations no referencia Customers ni
/// Messaging, así que acá se resuelve el cliente de la conversación —creándolo y colgándolo por su BSUID si no tiene, como la ingesta—, la
/// identidad de canal que respondió y el <c>wamid</c> del entrante, que son la prueba de la fila.
/// <para>
/// Cada llamada relee la conversación: la respuesta se registra para el cliente que la conversación tiene al responder, no el que tenía al
/// preguntar (una fusión o una reasignación en el medio). Los logs llevan sólo tenant y conversación, nunca datos del cliente.
/// </para>
/// </summary>
internal sealed partial class AutomationDataConsents(
    IConversationQueries queries,
    IConversationRepository conversations,
    IMessagingCustomerDirectory customers,
    IAutomationInbox inbox,
    ICustomerDataConsents consents,
    IMessagingNotifier notifier,
    IClock clock,
    ILogger<AutomationDataConsents> logger) : IAutomationDataConsents
{
    // Sólo el tipo de la excepción: su mensaje puede traer el BSUID o el teléfono (el detalle de una violación de índice, por ejemplo).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The customer of conversation {ConversationId} of tenant {TenantId} could not be ensured or linked ({ExceptionType}); its data consent is unavailable.")]
    private static partial void LogCustomerUnavailable(ILogger logger, Guid tenantId, Guid conversationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live notification for conversation {ConversationId} of tenant {TenantId} failed after its customer was linked.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    public async Task<DataConsentCheck> CheckAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        if (await queries.FindAsync(tenantId, conversationId, cancellationToken) is not { } row
            || ChannelOf(row) is null
            || await CustomerOfAsync(row, cancellationToken) is not { } customerId)
        {
            return DataConsentCheck.Unavailable;
        }

        return await consents.HasCurrentAcceptanceAsync(tenantId, customerId, cancellationToken)
            ? DataConsentCheck.Accepted
            : DataConsentCheck.NotAccepted;
    }

    public async Task<bool> RecordAsync(DataConsentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (await queries.FindAsync(record.TenantId, record.ConversationId, cancellationToken) is not { UserId: { } userId } row
            || ChannelOf(row) is not { } channel)
        {
            return false;
        }

        // El entrante antes que el cliente: una respuesta que no existe no tiene por qué crear a nadie. Y de esta conversación (final review
        // F2): un entrante de otra no prueba que esta identidad respondió.
        if (await inbox.ReadInboundAsync(record.TenantId, record.ReplyMessageId, cancellationToken) is not { } inbound
            || inbound.ConversationId != row.Id
            || await CustomerOfAsync(row, cancellationToken) is not { } customerId)
        {
            return false;
        }

        // Final review F1: el mensaje que citó la respuesta (la pregunta cuyo botón se tocó) queda en la fila, como el resto de la prueba.
        var identity = new DataConsentIdentity(row.Id, channel, userId, row.WaId);
        var evidence = new ChatbotConsentEvidence(
            record.ShownText, record.PromptMessageId, record.ReplyMessageId, inbound.Wamid, record.ChatbotId, record.ChatbotName,
            record.ChatbotVersionId, record.ChatbotVersionNumber, record.NodeId, inbound.ReplyToMessageId);
        var outcome = await consents.RecordFromChatbotAsync(
            new ChatbotDataConsentRecord(record.TenantId, customerId, ToEvent(record.Decision), inbound.OccurredAt, identity, evidence),
            cancellationToken);
        return outcome switch
        {
            DataConsentRecordOutcome.Recorded or DataConsentRecordOutcome.AlreadyRecorded => true,
            DataConsentRecordOutcome.CustomerMissing => false,
            _ => throw new ArgumentOutOfRangeException(nameof(record), outcome, "Unknown data consent record outcome."),
        };
    }

    /// <summary>Final review F3: un canal de Mensajería que Customers todavía no conoce es «no se puede registrar» —el bot sale por
    /// «No acepto» sin preguntar—, no una excepción: ésa haría fallar cada reintento de la unidad y el toque se perdería igual.</summary>
    private static DataConsentChannel? ChannelOf(ConversationRow row) => row.Channel switch
    {
        ConversationChannel.WhatsApp => DataConsentChannel.WhatsApp,
        ConversationChannel.Instagram => DataConsentChannel.Instagram,
        _ => null,
    };

    private static DataConsentEvent ToEvent(DataConsentDecision decision) => decision switch
    {
        DataConsentDecision.Accepted => DataConsentEvent.Accepted,
        DataConsentDecision.Rejected => DataConsentEvent.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "The chatbot only records an acceptance or a rejection."),
    };

    /// <summary>D-7: el cliente de la conversación; sin él y con BSUID, se crea (o encuentra) y se cuelga. <c>null</c> = no hay cliente ni
    /// identidad de canal, o crearlo o colgarlo falló: el bot sale por «No acepto» sin preguntar.</summary>
    private async Task<Guid?> CustomerOfAsync(ConversationRow row, CancellationToken cancellationToken)
    {
        if (row.UserId is null)
        {
            return null;
        }

        if (row.CustomerId is { } linked)
        {
            return linked;
        }

        MessagingEnsuredCustomer ensured;
        Guid? current;
        try
        {
            ensured = await customers.EnsureAsync(
                row.TenantId, new MessagingContact(row.UserId, row.WaId, row.ProfileName, row.Username, row.Channel), cancellationToken);
            current = await conversations.LinkCustomerAsync(
                row.TenantId, row.Id, ensured.CustomerId, ensured.Created, clock.UtcNow, cancellationToken);
        }
        catch (Exception exception) when (!MustPropagate(exception))
        {
            LogCustomerUnavailable(logger, row.TenantId, row.Id, exception.GetType().Name);
            return null;
        }

        // La conversación no tenía cliente al leerla y quedó con éste: lo colgó esta llamada (o la ingesta, a la vez, con el mismo, y
        // entonces el aviso se repite sin daño). La bandeja se entera como cuando la ingesta lo cuelga.
        if (current == ensured.CustomerId)
        {
            await NotifyAsync(row.TenantId, row.Id, cancellationToken);
        }

        return current;
    }

    /// <summary>
    /// Lo que no es «no se pudo» sino «todavía no»: la cancelación (el worker que se apaga), un conflicto de concurrencia y un fallo
    /// transitorio de la base (conexión caída, timeout, deadlock o serialización: <see cref="DbException.IsTransient"/>, que Npgsql
    /// marca). Suben y la unidad del motor se reintenta, en vez de registrar que el titular salió por «No acepto» sin que se le preguntara.
    /// </summary>
    private static bool MustPropagate(Exception exception)
    {
        if (exception is OperationCanceledException or RequestConcurrencyException)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ruling P2-R22: el vínculo ya se confirmó; un aviso que falla se registra y no sube. La cancelación sí sube.</summary>
    private async Task NotifyAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, exception, tenantId, conversationId);
        }
    }
}
