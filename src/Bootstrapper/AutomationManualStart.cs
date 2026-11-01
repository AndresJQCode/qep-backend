using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application.Engine;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-31: adaptador del puerto de arranque manual. Mensajería no ve Automations; aquí se traduce el veredicto de
/// <see cref="ManualRunStarter"/> a los errores de Mensajería (decisión 7). Corre con el tracker del request a propósito: sólo lee y
/// sostiene el candado; la unidad corre en el worker.</summary>
internal sealed partial class AutomationManualStart(ManualRunStarter starter, ILogger<AutomationManualStart> logger) : IAutomationManualStart
{
    // Sin el contenido de ningún mensaje: sólo ids; la excepción va aparte para conservar la traza.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant start state of conversation {ConversationId} of tenant {TenantId} could not be evaluated; the thread is returned without the start button.")]
    private static partial void LogStateFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    /// <summary>Mejor esfuerzo, como <c>AutomationConversations.NotifyAsync</c> (P2-R22): el resumen se arma después del commit de tomar,
    /// devolver, pausar, reanudar, resolver, reabrir y enviar una plantilla, y un fallo aquí contestaría 500 por un cambio ya hecho (y
    /// tumbaría el detalle del hilo). <c>null</c> es la dirección segura: sin botón, queda «Devolver al asistente». La cancelación sube.</summary>
    public async Task<string?> FindStateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            return await starter.EvaluateAsync(tenantId, conversationId, cancellationToken) switch
            {
                ManualStartVerdict.Available => AutomationStartStates.Available,
                ManualStartVerdict.WindowClosed => AutomationStartStates.WindowClosed,
                ManualStartVerdict.NoChatbot => AutomationStartStates.NoChatbot,
                _ => null,
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStateFailed(logger, exception, tenantId, conversationId);
            return null;
        }
    }

    public async Task StartAsync(
        Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken)
    {
        var verdict = await starter.StartAsync(tenantId, conversationId, commit, cancellationToken);
        switch (verdict)
        {
            case ManualStartVerdict.Available:
                return;
            case ManualStartVerdict.Busy:
                throw new RequestConcurrencyException("concurrency.conflict", "The conversation changed while the assistant was starting; reload and try again.");
            case ManualStartVerdict.NotFound:
                throw new ResourceNotFoundException(MessagingErrorCodes.ConversationNotFound, $"Conversation '{conversationId}' was not found.");
            case ManualStartVerdict.NotOpen:
                throw Rejected(MessagingErrorCodes.ConversationNotOpen, "The conversation is not open.");
            case ManualStartVerdict.Blocked:
                throw Rejected(MessagingErrorCodes.ContactBlocked, "The contact is blocked.");
            case ManualStartVerdict.UnsupportedChannel:
                throw Rejected(MessagingErrorCodes.ChannelUnsupported, "Only a WhatsApp conversation can start the assistant.");
            case ManualStartVerdict.WindowClosed:
                throw Rejected(MessagingErrorCodes.WindowClosed, "The customer window is closed.");
            case ManualStartVerdict.BotAttending:
            case ManualStartVerdict.StartPending:
                throw Rejected(MessagingErrorCodes.AutomationAlreadyActive, "The assistant is already attending the conversation or about to.");
            case ManualStartVerdict.ModuleDisabled:
                throw Rejected(MessagingErrorCodes.AutomationNotAvailable, "The automations module is not enabled for this tenant.");
            case ManualStartVerdict.HasAdvisor:
                throw Rejected(MessagingErrorCodes.AutomationHasAdvisor, "A conversation with an advisor is not attended by the assistant.");
            case ManualStartVerdict.NoChatbot:
                throw Rejected(MessagingErrorCodes.AutomationNoChatbot, "The connection has no active chatbot.");
            default:
                throw new InvalidOperationException($"Unhandled manual start verdict '{verdict}'.");
        }
    }

    private static MessagingDomainException Rejected(string code, string message) => new(code, message);
}
