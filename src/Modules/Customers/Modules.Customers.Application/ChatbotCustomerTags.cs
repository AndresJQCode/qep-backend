using BuildingBlocks.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Chatbot CRM action spec (2026-11-19): what a one-tag change by the chatbot did.</summary>
public enum ChatbotTagOutcome
{
    Added,
    AlreadyTagged,
    Removed,
    NotTagged,
    CustomerMissing,
    TagMissing,
    LimitReached,
}

/// <summary>
/// Chatbot CRM action spec (2026-11-19): the chatbot adds or removes one tag as the system. No member and no permission check
/// (like <see cref="ICustomerDataConsents"/>): the flow was published by someone allowed to, and the engine is the actor. Each call
/// is idempotent (a tag already there, or already gone, writes nothing and audits nothing), and works on any customer of the
/// tenant, Incomplete or inactive included; a merged one is tagged on the survivor.
/// </summary>
public interface IChatbotCustomerTags
{
    Task<ChatbotTagOutcome> AddAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken);

    Task<ChatbotTagOutcome> RemoveAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken);
}

public sealed class ChatbotCustomerTags(
    ICustomerDataConsentRepository customerLocks,
    ICustomerTagRepository tags,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IClock clock) : IChatbotCustomerTags
{
    private const string Success = "success";

    // The system has no user: the assignment carries an empty id, as the audit row carries an empty actor.
    private static readonly Guid SystemUser = Guid.Empty;

    public Task<ChatbotTagOutcome> AddAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, customerId, tagId, add: true, cancellationToken);

    public Task<ChatbotTagOutcome> RemoveAsync(Guid tenantId, Guid customerId, Guid tagId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, customerId, tagId, add: false, cancellationToken);

    private async Task<ChatbotTagOutcome> ChangeAsync(
        Guid tenantId, Guid customerId, Guid tagId, bool add, CancellationToken cancellationToken)
    {
        // Every return without CommitAsync rolls the transaction back when it is released.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // FOR UPDATE on the customer (and its survivor, one hop): two chatbot changes of the same customer queue up, so the
        // count against the limit and the "already there" check hold until the commit. Not Customer.Update: an Incomplete
        // customer would fail its validation, and a tag is not a field of the aggregate.
        if (await customerLocks.LockCustomerAsync(tenantId, customerId, cancellationToken) is not { } lockedId)
        {
            return ChatbotTagOutcome.CustomerMissing;
        }

        var tag = new TagId(tagId);
        // Tenant-scoped: a tag of another tenant is as absent as a deleted one.
        if ((await tags.ListByIdsAsync(tenantId, [tag], cancellationToken)).Count == 0)
        {
            return ChatbotTagOutcome.TagMissing;
        }

        var customer = new CustomerId(lockedId);
        var current = await tags.ListAssignmentsAsync(tenantId, customer, cancellationToken);
        var existing = current.FirstOrDefault(assignment => assignment.TagId == tag);
        var now = clock.UtcNow;

        if (add)
        {
            if (existing is not null)
            {
                return ChatbotTagOutcome.AlreadyTagged;
            }

            if (current.Count >= CustomerTagAssignment.MaxPerCustomer)
            {
                return ChatbotTagOutcome.LimitReached;
            }

            tags.AddAssignment(CustomerTagAssignment.Create(customer, tag, tenantId, SystemUser, now));
        }
        else
        {
            if (existing is null)
            {
                return ChatbotTagOutcome.NotTagged;
            }

            tags.RemoveAssignment(existing);
        }

        // One tag changed is a change of the set: the same action the screen audits.
        auditPublisher.PublishBySystem(
            tenantId, CustomerAuditActions.TagsChanged, lockedId.ToString(), Success, now, CustomerAuditSources.Chatbot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return add ? ChatbotTagOutcome.Added : ChatbotTagOutcome.Removed;
    }
}
