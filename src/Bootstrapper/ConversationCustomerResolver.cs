using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application.Engine;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Who the conversation's customer is, for a chatbot block that acts on it.</summary>
internal abstract record ConversationCustomer
{
    private ConversationCustomer()
    {
    }

    /// <summary>The conversation has no channel identity: there is nobody to create or find.</summary>
    public sealed record NoUser : ConversationCustomer;

    /// <summary>Creating or linking the customer failed (not transiently): there is somebody, but no customer to act on.</summary>
    public sealed record Unavailable : ConversationCustomer;

    public sealed record Linked(Guid CustomerId) : ConversationCustomer;
}

/// <summary>
/// Spec 2026-10-26 D-7 and spec 2026-11-19 (chatbot CRM action) «Which customer»: the conversation's customer; without one and with a
/// BSUID it is created (or found) and linked, as the ingestion does. Shared by «Política de datos» and «Acción de CRM».
/// <para>Logs carry only tenant and conversation, never customer data.</para>
/// </summary>
internal sealed partial class ConversationCustomerResolver(
    IConversationRepository conversations,
    IMessagingCustomerDirectory customers,
    IMessagingNotifier notifier,
    IClock clock,
    ILogger<ConversationCustomerResolver> logger)
{
    // Only the exception type: its message may carry the BSUID or the phone (an index violation's detail, for instance).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The customer of conversation {ConversationId} of tenant {TenantId} could not be ensured or linked ({ExceptionType}).")]
    private static partial void LogCustomerUnavailable(ILogger logger, Guid tenantId, Guid conversationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live notification for conversation {ConversationId} of tenant {TenantId} failed after its customer was linked.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    public async Task<ConversationCustomer> ResolveAsync(ConversationRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.UserId is null)
        {
            return new ConversationCustomer.NoUser();
        }

        if (row.CustomerId is { } linked)
        {
            return new ConversationCustomer.Linked(linked);
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
        // «Not yet» (a cancellation, a conflict, a transient database failure) goes up and the engine unit is retried.
        catch (Exception exception) when (!TransientFailures.MustPropagate(exception))
        {
            LogCustomerUnavailable(logger, row.TenantId, row.Id, exception.GetType().Name);
            return new ConversationCustomer.Unavailable();
        }

        // The conversation had no customer when read and now has this one: this call linked it (or the ingestion did, at the same
        // time and with the same one, and then the notice repeats harmlessly). The inbox learns it as when the ingestion links it.
        if (current == ensured.CustomerId)
        {
            await NotifyAsync(row.TenantId, row.Id, cancellationToken);
        }

        return current is { } customerId ? new ConversationCustomer.Linked(customerId) : new ConversationCustomer.Unavailable();
    }

    /// <summary>Ruling P2-R22: the link is already confirmed; a failed notice is logged and does not go up. A cancellation does.</summary>
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
