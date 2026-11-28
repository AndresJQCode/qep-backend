using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <remarks>Por variante (spec 2026-10-20 §4): <c>VariantId</c> es la clave de la línea.</remarks>
/// <summary>Una línea deseada. <c>UnitPrice</c> (IVA incluido) sólo vale en un pedido mayorista sin aprobar (D-4); ausente, la
/// línea conserva su precio o, si es nueva, toma el de lista (D-5).</summary>
public sealed record OrderItemAddition(Guid VariantId, decimal Quantity, decimal? UnitPrice = null);

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
    ITenantPriceRounding priceRounding,
    IQuotationProductLookup productLookup,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderItemsCommand> validator,
    IOrderVariantCostLookup costLookup,
    IWholesaleLinePricing wholesalePricing,
    IQuotationLifecycleEventPublisher lifecycleEvents)
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
        if (!order.IsEditable)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.NotEditable,
                "Products can only be added while the order is editable.");
        }

        // Antes de leer el catálogo (spec 2026-11-26 §6.6).
        ProjectOrderLines.EnsureAcceptsNewItems(order);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var previousNetTotal = order.NetTotal;
        // Una sola lectura del catálogo para toda la tanda. Los rechazos no se adelantan: siguen
        // abajo, línea por línea, en el mismo orden.
        var products = await QuotationProductPricingResolver.FindVariantsAsync(
            pricingLookup, command.TenantId, command.ToAdd.Select(addition => addition.VariantId), cancellationToken,
            QuotationPriceChannels.For(order.Source));
        // Un pedido pagado sigue editable y lo que entra ya cuenta como vendido: congela al llegar (§4.3).
        var costs = await OrderLineCostsLoader.LoadForAdditionsAsync(
            costLookup, command.TenantId, order, command.ToAdd.Select(addition => addition.VariantId), cancellationToken);
        // D-5: en un pedido mayorista la línea nueva toma el precio de lista del distribuidor, sin escala; sin lista, el base.
        var listPrices = await OrderItemEdits.WholesaleListPricesAsync(
            wholesalePricing, order, command.TenantId, command.ToAdd.Select(addition => addition.VariantId).ToArray(),
            cancellationToken);
        foreach (var addition in command.ToAdd)
        {
            // Misma resolución que el cotizador: precio en la moneda del pedido, descuento por
            // escala para esa cantidad y tasa del producto.
            var pricing = QuotationProductPricingResolver.Resolve(
                products.GetValueOrDefault(addition.VariantId), command.TenantId, addition.VariantId,
                addition.Quantity, order.Currency, order.IsRetail);
            var wholesale = order.Source == OrderSource.Wholesale;
            order.AddItem(
                OrderItemId.New(), addition.VariantId, pricing.ProductId, addition.Quantity,
                wholesale ? listPrices.GetValueOrDefault(addition.VariantId, pricing.Pricing.UnitPrice) : pricing.Pricing.UnitPrice,
                wholesale ? 0m : pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, costs, updatedBy, now, pricing.Kind, pricing.SalesUnit);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged,
                updatedBy, OrderChangeSummary.ItemAdded(pricing.Name, addition.Quantity, format), now));
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "quotation.order.item_added",
                order.Id.ToString(), "success", now);
        }

        // Justo después de la última línea sumada: el WMS reemplaza su reserva con las líneas como quedaron.
        lifecycleEvents.PublishOrderEdited(order, now);
        order.CaptureProductSnapshots(
            await OrderItemProductLabel.ResolveMissingAsync(productLookup, command.TenantId, order, cancellationToken));
        // Sumar una línea mueve el tramo de las que agrupan con ella y puede cambiar el mínimo de
        // compra: el recálculo va antes de mirar el total.
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, command.TenantId, order, now, cancellationToken);
        // El total cambió: lo cargado en comprobantes no, pero el estado del pago sí puede.
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
