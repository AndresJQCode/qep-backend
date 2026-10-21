using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <remarks>Por variante (spec 2026-10-20 §4): <c>VariantId</c> es la clave de la línea.</remarks>
public sealed record OrderItemAddition(Guid VariantId, decimal Quantity);

/// <summary>Suma productos a un pedido pendiente (a pedido, 2026-09). Desde el spec 2026-10-15 §4.5
/// las líneas son del pedido: la cotización de origen no se toca.</summary>
public sealed record AddOrderItemsCommand(
    Guid TenantId,
    Guid OrderId,
    IReadOnlyList<OrderItemAddition> ToAdd) : ICommand<OrderDetailDto>;

public sealed class AddOrderItemsValidator : AbstractValidator<AddOrderItemsCommand>
{
    public AddOrderItemsValidator()
    {
        RuleForEach(command => command.ToAdd).ChildRules(item =>
        {
            item.RuleFor(addition => addition.VariantId).NotEmpty();
            item.RuleFor(addition => addition.Quantity).GreaterThan(0m);
        });
        RuleFor(command => command.ToAdd)
            .Must(toAdd => toAdd.Count > 0)
            .WithMessage("At least one product must be added.");
    }
}

public sealed class AddOrderItemsHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    IQuotationProductLookup productLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderItemsCommand> validator)
    : ICommandHandler<AddOrderItemsCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        AddOrderItemsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
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
            // Misma resolución que el cotizador: precio en la moneda del pedido, descuento por
            // escala para esa cantidad y tasa del producto.
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, command.TenantId, addition.VariantId, addition.Quantity,
                order.Currency, order.IsRetail, cancellationToken);
            order.AddItem(
                OrderItemId.New(), addition.VariantId, pricing.ProductId, addition.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, now);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged,
                updatedBy, OrderChangeSummary.ItemAdded(pricing.Name, addition.Quantity), now));
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "quotation.order.item_added",
                order.Id.ToString(), "success", now);
        }

        order.CaptureProductSnapshots(
            await OrderItemProductLabel.ResolveMissingAsync(productLookup, command.TenantId, order, cancellationToken));
        // Sumar una línea mueve el tramo de las que agrupan con ella y puede cambiar el mínimo de
        // compra: el recálculo va antes de mirar el total.
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, command.TenantId, order, now, cancellationToken);
        // El total cambió: lo cargado en comprobantes no, pero el estado del pago sí puede.
        order.RecalculatePaymentStatus(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
