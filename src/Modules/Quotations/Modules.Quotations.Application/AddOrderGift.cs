using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Suma un regalo a un pedido editable (spec 2026-11-21): una variante cualquiera, incluso la de una línea pagada,
/// a su precio de lista y con descuento fijo del 100 %.</summary>
public sealed record AddOrderGiftCommand(
    Guid TenantId,
    Guid OrderId,
    Guid VariantId,
    decimal Quantity) : ICommand<OrderDetailDto>;

public sealed class AddOrderGiftValidator : AbstractValidator<AddOrderGiftCommand>
{
    public AddOrderGiftValidator()
    {
        RuleFor(command => command.VariantId).NotEmpty();
        RuleFor(command => command.Quantity).GreaterThan(0m);
    }
}

public sealed class AddOrderGiftHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    ITenantPriceRounding priceRounding,
    IQuotationProductLookup productLookup,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderGiftCommand> validator,
    IOrderVariantCostLookup costLookup)
    : ICommandHandler<AddOrderGiftCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        AddOrderGiftCommand command,
        CancellationToken cancellationToken)
    {
        // Misma doble revisión que AddOrderItems: permiso y tenant primero; un pedido ajeno es 403, nunca 404.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        if (!order.IsEditable)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.NotEditable,
                "Gifts can only be added while the order is editable.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var previousNetTotal = order.NetTotal;

        var products = await QuotationProductPricingResolver.FindVariantsAsync(
            pricingLookup, command.TenantId, [command.VariantId], cancellationToken, QuotationPriceChannels.For(order.Source));
        var costs = await OrderLineCostsLoader.LoadForAdditionsAsync(
            costLookup, command.TenantId, order, [command.VariantId], cancellationToken);
        // Un pedido mayorista no llega acá con un regalo: Order.AddGift lo rechaza.
        // isRetail: true salta las escalas a propósito: el regalo va a precio de lista y su descuento es el fijo del 100 %,
        // así que un producto con las escalas a medio configurar no impide regalarlo.
        var pricing = QuotationProductPricingResolver.Resolve(
            products.GetValueOrDefault(command.VariantId), command.TenantId, command.VariantId,
            command.Quantity, order.Currency, isRetail: true);

        order.AddGift(
            OrderItemId.New(), command.VariantId, pricing.ProductId, command.Quantity, pricing.Pricing.UnitPrice,
            pricing.Pricing.TaxPercentage, costs, updatedBy, now, pricing.Kind, pricing.SalesUnit);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged,
            updatedBy, OrderChangeSummary.GiftAdded(pricing.Name, command.Quantity, format), now));
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "quotation.order.gift_added",
            order.Id.ToString(), "success", now);

        order.CaptureProductSnapshots(
            await OrderItemProductLabel.ResolveMissingAsync(productLookup, command.TenantId, order, cancellationToken));
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, command.TenantId, order, now, cancellationToken);
        if (order.RecalculatePaymentStatus(previousNetTotal, now) is { } paymentChange)
        {
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PaymentStatusRecalculated, updatedBy,
                OrderChangeSummary.PaymentStatusRecalculated(paymentChange, format), now));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
