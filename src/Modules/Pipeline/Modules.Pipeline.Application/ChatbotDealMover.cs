using BuildingBlocks.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Chatbot CRM action spec (2026-11-19): what moving the conversation's deal did.</summary>
public enum ChatbotDealMoveOutcome
{
    Moved,
    AlreadyInStage,
    NoOpenDeal,
    SeveralOpenDeals,
    StageNotOpen,
    StageMissing,
    ModuleOff,
    Conflict,
}

/// <summary>
/// Chatbot CRM action spec (2026-11-19) «Which deal»: the chatbot moves a deal as the system (<see cref="DealActor.ForSystem"/>,
/// audit <c>bySystem</c> with source <c>chatbot</c>), with no member and no permission check: the flow was published by someone
/// allowed to. Unlike <see cref="QuotationEventApplier"/> it saves in its own unit of work, and it never creates anything.
/// </summary>
public interface IChatbotDealMover
{
    /// <param name="customerId">The conversation's linked customer, or <c>null</c> when it has none: then only the deal born
    /// from this conversation can move.</param>
    Task<ChatbotDealMoveOutcome> MoveAsync(
        Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId, CancellationToken cancellationToken);
}

public sealed class ChatbotDealMover(
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IPipelineCustomerDirectory customers,
    IDealRepository deals,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    IClock clock) : IChatbotDealMover
{
    // The engine passes no version: the deal is loaded fresh, and a conflict with a person moving it at the same time is
    // re-read and tried once more before giving up.
    private const int Attempts = 2;

    public async Task<ChatbotDealMoveOutcome> MoveAsync(
        Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId, CancellationToken cancellationToken)
    {
        // Strict: no row for the tenant is as off as an inactive module (the editor offers the block only with the module on).
        if (await tenantModules.FindAsync(tenantId, cancellationToken) is not { } modules || !modules.IsEnabled(TenantModuleKeys.Pipeline))
        {
            return ChatbotDealMoveOutcome.ModuleOff;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryMoveAsync(tenantId, customerId, conversationId, stageId, cancellationToken);
            }
            catch (RequestConcurrencyException exception) when (exception.Code == "concurrency.conflict")
            {
                unitOfWork.DiscardChanges();
                if (attempt == Attempts)
                {
                    return ChatbotDealMoveOutcome.Conflict;
                }
            }
        }
    }

    private async Task<ChatbotDealMoveOutcome> TryMoveAsync(
        Guid tenantId, Guid? customerId, Guid conversationId, Guid stageId, CancellationToken cancellationToken)
    {
        var pipeline = await provisioner.EnsureAsync(tenantId, cancellationToken);

        // The target first: a stage deleted or closed after publishing is a broken flow whoever the customer is, so it is
        // reported as such and not hidden behind «no open deal».
        if (pipeline.FindStage(stageId) is not { } stage)
        {
            return ChatbotDealMoveOutcome.StageMissing;
        }

        if (stage.Kind != StageKind.Open)
        {
            return ChatbotDealMoveOutcome.StageNotOpen;
        }

        var (outcome, deal) = await ResolveDealAsync(tenantId, customerId, conversationId, cancellationToken);
        if (deal is null)
        {
            return outcome;
        }

        var now = clock.UtcNow;
        if (!deal.MoveTo(pipeline, stageId, DealActor.ForSystem, now))
        {
            return ChatbotDealMoveOutcome.AlreadyInStage;
        }

        audit.Record(
            tenantId, Guid.Empty, bySystem: true, PipelineAuditActions.DealMoved, PipelineAuditActions.DealResource, deal.Id, now,
            PipelineAuditSources.Chatbot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ChatbotDealMoveOutcome.Moved;
    }

    // The deal born from this conversation wins only if it is the conversation customer's (or the conversation has no
    // customer); otherwise the customer's only open deal. Never a guess, never another customer's deal.
    private async Task<(ChatbotDealMoveOutcome Outcome, Deal? Deal)> ResolveDealAsync(
        Guid tenantId, Guid? customerId, Guid conversationId, CancellationToken cancellationToken)
    {
        // A merge moved the merged customer's deals to the survivor: compare and list against the survivor.
        Guid? customer = customerId is { } id ? await customers.ResolveSurvivorAsync(tenantId, id, cancellationToken) : null;

        var fromConversation = await deals.FindOpenFromConversationAsync(tenantId, conversationId, cancellationToken);
        if (fromConversation is not null && (customer is null || fromConversation.CustomerId == customer))
        {
            return (ChatbotDealMoveOutcome.Moved, fromConversation);
        }

        if (customer is not { } owner)
        {
            return (ChatbotDealMoveOutcome.NoOpenDeal, null);
        }

        var open = await deals.ListOpenByCustomerAsync(tenantId, owner, cancellationToken);
        return open.Count switch
        {
            0 => (ChatbotDealMoveOutcome.NoOpenDeal, null),
            1 => (ChatbotDealMoveOutcome.Moved, open[0]),
            _ => (ChatbotDealMoveOutcome.SeveralOpenDeals, null),
        };
    }
}
