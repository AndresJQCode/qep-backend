using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record OrderItemAddition(Guid ProductId, decimal Quantity);
public sealed record OrderItemsAddedResult(OrderDto Order, QuotationDto Quotation);

public sealed record AddOrderItemsCommand(
    Guid TenantId,
    Guid QuotationId,
    IReadOnlyList<OrderItemAddition> ToAdd) : ICommand<OrderItemsAddedResult>;

public sealed class AddOrderItemsValidator : AbstractValidator<AddOrderItemsCommand>
{
    public AddOrderItemsValidator()
    {
        RuleForEach(command => command.ToAdd).ChildRules(item =>
        {
            item.RuleFor(addition => addition.ProductId).NotEmpty();
            item.RuleFor(addition => addition.Quantity).GreaterThan(0m);
        });
        RuleFor(command => command.ToAdd)
            .Must(toAdd => toAdd.Count > 0)
            .WithMessage("At least one product must be added.");
    }
}

public sealed class AddOrderItemsHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderItemsCommand> validator)
    : ICommandHandler<AddOrderItemsCommand, OrderItemsAddedResult>
{
    public async Task<OrderItemsAddedResult> HandleAsync(
        AddOrderItemsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var order = await orderRepository.FindByQuotationIdAsync(
            command.TenantId, quotation.Id, cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "Products can only be added while the order is pending.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        foreach (var addition in command.ToAdd)
        {
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, command.TenantId, addition.ProductId, addition.Quantity,
                quotation.Currency, cancellationToken);

            quotation.AddItemAfterConversion(
                QuotationItemId.New(), addition.ProductId, addition.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, now);

            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(), quotation.Id, QuotationHistoryEventType.Edited,
                updatedBy, QuotationChangeSummary.ItemAdded(pricing.Name, addition.Quantity), now));
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "quotation.order.item_added",
                quotation.Id.ToString(), "success", now);
        }

        order.RecalculatePaymentStatus(quotation.Total, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new OrderItemsAddedResult(order.ToDto(), quotation.ToDto());
    }
}
