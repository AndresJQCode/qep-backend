using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using Modules.Customers.Application;
using Modules.Messaging.Application;
using Modules.Pipeline.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-19 (chatbot CRM action): the «Acción de CRM» block over Customers and Pipeline. Automations references neither, so
/// the conversation's customer is resolved here and the system entry points answer an outcome this adapter maps to a
/// <see cref="CrmResultCodes"/> code.
/// <para>
/// «Which customer»: the tag operations act on the conversation's customer, creating and linking one when it has none (as
/// «Política de datos» does); moving a deal never creates one and passes the linked customer as it is. Each call rereads the
/// conversation. A broken rule is <c>failed</c>; a cancellation, a concurrency conflict or a transient database failure goes up
/// (<see cref="Modules.Automations.Application.Engine.TransientFailures.MustPropagate"/>) so the engine unit is retried. Logs carry only tenant and conversation.
/// </para>
/// </summary>
internal sealed partial class AutomationCrm(
    IConversationQueries queries,
    ConversationCustomerResolver resolver,
    ITenantModules tenantModules,
    IChatbotCustomerTags tags,
    IChatbotDealMover deals,
    ILogger<AutomationCrm> logger) : IAutomationCrm
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The CRM action of conversation {ConversationId} of tenant {TenantId} failed ({ExceptionType}).")]
    private static partial void LogFailed(ILogger logger, Guid tenantId, Guid conversationId, string exceptionType);

    public Task<string> AddTagAsync(CrmActionContext context, Guid tagId, CancellationToken cancellationToken) =>
        ChangeTagAsync(context, customerId => tags.AddAsync(context.TenantId, customerId, tagId, cancellationToken), cancellationToken);

    public Task<string> RemoveTagAsync(CrmActionContext context, Guid tagId, CancellationToken cancellationToken) =>
        ChangeTagAsync(context, customerId => tags.RemoveAsync(context.TenantId, customerId, tagId, cancellationToken), cancellationToken);

    public async Task<string> MoveDealAsync(CrmActionContext context, Guid stageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (await queries.FindAsync(context.TenantId, context.ConversationId, cancellationToken) is not { } row)
            {
                return CrmResultCodes.Failed;
            }

            // Never creates a customer: without one, only the deal born from this conversation can move.
            var outcome = await deals.MoveAsync(context.TenantId, row.CustomerId, row.Id, stageId, cancellationToken);
            return outcome switch
            {
                ChatbotDealMoveOutcome.Moved => CrmResultCodes.Moved,
                ChatbotDealMoveOutcome.AlreadyInStage => CrmResultCodes.AlreadyInStage,
                ChatbotDealMoveOutcome.NoOpenDeal => CrmResultCodes.NoOpenDeal,
                ChatbotDealMoveOutcome.SeveralOpenDeals => CrmResultCodes.SeveralOpenDeals,
                ChatbotDealMoveOutcome.StageNotOpen => CrmResultCodes.StageNotOpen,
                ChatbotDealMoveOutcome.StageMissing => CrmResultCodes.StageMissing,
                ChatbotDealMoveOutcome.ModuleOff => CrmResultCodes.ModuleOff,
                ChatbotDealMoveOutcome.Conflict => CrmResultCodes.DealConflict,
                _ => throw new ArgumentOutOfRangeException(nameof(stageId), outcome, "Unknown deal move outcome."),
            };
        }
        catch (DomainException exception)
        {
            LogFailed(logger, context.TenantId, context.ConversationId, exception.GetType().Name);
            return CrmResultCodes.Failed;
        }
    }

    private async Task<string> ChangeTagAsync(
        CrmActionContext context, Func<Guid, Task<ChatbotTagOutcome>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            // The module first: with Customers off nobody is created, not even to say «module off».
            if (await tenantModules.FindAsync(context.TenantId, cancellationToken) is not { } modules
                || !modules.IsEnabled(TenantModuleKeys.Customers))
            {
                return CrmResultCodes.ModuleOff;
            }

            if (await queries.FindAsync(context.TenantId, context.ConversationId, cancellationToken) is not { } row)
            {
                return CrmResultCodes.Failed;
            }

            return await resolver.ResolveAsync(row, cancellationToken) switch
            {
                ConversationCustomer.NoUser => CrmResultCodes.NoCustomer,
                ConversationCustomer.Unavailable => CrmResultCodes.CustomerUnavailable,
                ConversationCustomer.Linked linked => CodeOf(await change(linked.CustomerId)),
                _ => throw new InvalidOperationException("Unknown conversation customer."),
            };
        }
        catch (DomainException exception)
        {
            LogFailed(logger, context.TenantId, context.ConversationId, exception.GetType().Name);
            return CrmResultCodes.Failed;
        }
    }

    private static string CodeOf(ChatbotTagOutcome outcome) => outcome switch
    {
        ChatbotTagOutcome.Added => CrmResultCodes.Added,
        ChatbotTagOutcome.AlreadyTagged => CrmResultCodes.AlreadyTagged,
        ChatbotTagOutcome.Removed => CrmResultCodes.Removed,
        ChatbotTagOutcome.NotTagged => CrmResultCodes.NotTagged,
        ChatbotTagOutcome.TagMissing => CrmResultCodes.TagMissing,
        ChatbotTagOutcome.LimitReached => CrmResultCodes.TagLimit,
        // The customer was merged away or deleted between the conversation's read and the lock.
        ChatbotTagOutcome.CustomerMissing => CrmResultCodes.CustomerUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown tag outcome."),
    };
}
