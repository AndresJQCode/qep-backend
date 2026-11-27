using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>El cálculo previo de «Editar» pedido (spec 2026-09-17, decisión 4): aplica las ediciones
/// a una copia sin tracking y devuelve el pedido como quedaría, sin guardar nada.</summary>
public sealed record PreviewOrderEditsQuery(
    Guid TenantId,
    Guid OrderId,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes,
    int? GlobalScaleFloor,
    bool IsRetail,
    ShippingRequest? Shipping = null,
    OrderBillingEditRequest? Billing = null,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    QuotationBillingAccountRequest? BillingAccount = null,
    OperationCityChoice OperationCity = default,
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    DateOnly? OrderDate = null,
    Guid? BillingCompanyId = null,
    IReadOnlyList<OrderItemAddition>? Gifts = null,
    OrderDeliveryEditRequest? Delivery = null) : IQuery<OrderDetailDto>, IOrderEdits;

public sealed class PreviewOrderEditsValidator : OrderEditsValidator<PreviewOrderEditsQuery>
{
    public PreviewOrderEditsValidator()
        : base(requireFileIds: false, requireBillingIdentification: false)
    {
    }
}

public sealed class PreviewOrderEditsHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    ITenantPriceRounding priceRounding,
    ShippingResolver shippingResolver,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewOrderEditsQuery> validator,
    IQuotationCompanyLookup companyLookup,
    IQuotationWithholdingLookup withholdingLookup,
    ITenantClock tenantClock,
    IOrderVariantCostLookup costLookup,
    IQuotationGeographyLookup geographyLookup,
    IWholesaleLinePricing wholesalePricing,
    IQuotationCustomerLookup customerLookup,
    IExternalOrderDeliveryCheck deliveryCheck)
    : IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        PreviewOrderEditsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderManage);
        // Sin tracking: el preview muta el agregado en memoria y nada de eso puede llegar a un
        // SaveChangesAsync del mismo scope. Como en el guardado, a veces hay que saber el tipo del pedido antes de validar.
        var orderId = new OrderId(query.OrderId);
        var order = OrderEditsValidation.NeedsTheOrder(query)
            ? await orderRepository.FindUntrackedByIdAsync(query.TenantId, orderId, cancellationToken)
            : null;
        await OrderEditsValidation.ValidateAsync(validator, query, order, cancellationToken);

        order ??= await orderRepository.FindUntrackedByIdAsync(query.TenantId, orderId, cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);
        // Mismo orden que el guardado: el motivo exacto de la entrega antes que la guarda general.
        if (query.Delivery is not null)
        {
            order.EnsureDeliveryEditable();
        }

        if (!order.IsEditable)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.NotEditable,
                "An order can only be edited while it is open, not prepared and not invoiced.");
        }

        // Mismo candado que el guardado (spec 2026-11-26 §12-C).
        var keepsProjectLine = ProjectOrderLines.KeepsTheLine(order, query.Items, query.Gifts);
        var storedVersion = order.Version;
        var previousNetTotal = order.NetTotal;
        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        if (!keepsProjectLine)
        {
            await OrderItemEdits.ApplyAsync(
                order, query.Items, pricingLookup, costLookup, wholesalePricing, query.TenantId, updatedBy, now,
                query.IsRetail, cancellationToken);
            // Mismo orden que el guardado (spec 2026-11-23): los regalos después de las pagadas, y null no los toca.
            await OrderGiftEdits.ApplyAsync(
                order, query.Gifts, pricingLookup, costLookup, query.TenantId, updatedBy, now, cancellationToken);
        }

        foreach (var proofId in query.Proofs.RemoveIds)
        {
            order.RemovePaymentProof(new OrderPaymentProofId(proofId), now);
        }

        order.CorrectPaymentProofs(
            query.Proofs.Update
                .Select(update => new OrderPaymentProofAmountUpdate(
                    new OrderPaymentProofId(update.ProofId), update.Amount, PaidOn: update.PaidOn))
                .ToArray(),
            now);
        // Sin archivo todavía (el preview no lo exige): un id cualquiera sirve para sumar el monto.
        order.AttachPaymentProofs(
            query.Proofs.Add
                .Select(addition => new OrderPaymentProofInput(
                    Guid.CreateVersion7(), addition.Amount, PaidOn: addition.PaidOn))
                .ToArray(),
            updatedBy,
            now);
        order.UpdateNotes(query.Notes, now);
        // Las mismas reglas que el guardado (spec 2026-11-16 §4): una fecha futura es 422 también acá.
        if (query.OrderDate is { } orderDate)
        {
            order.ChangeOrderDate(orderDate, (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today, now);
        }

        if (!keepsProjectLine && order.IsRetail != query.IsRetail)
        {
            order.SetIsRetail(query.IsRetail, updatedBy, now);
        }

        if (!keepsProjectLine && order.GlobalScaleFloor != query.GlobalScaleFloor)
        {
            order.SetGlobalScaleFloor(query.GlobalScaleFloor, updatedBy, now);
        }

        // Mismo orden que el guardado: la entrega antes de la facturación y del envío, con las mismas reglas salvo la
        // parte completa, que el preview no exige mientras la persona teclea.
        await OrderDeliveryEdits.ApplyAsync(
            order, query.Delivery, strict: false, customerLookup, geographyLookup, deliveryCheck, query.TenantId, updatedBy, now,
            cancellationToken);

        // Mismo orden que el guardado: la facturación mueve la retención y el excedente de IVA, así que va antes
        // de recalcular. Sin la regla NIT ni las exigencias de fila completa: el preview no las aplica mientras la persona teclea.
        OrderBillingEdits.Apply(
            order, query.Billing, strict: false, updatedBy, now);

        // Las mismas reglas que el guardado (D21–D23): una tasa de otro país es 422 también acá, para que la vista previa
        // no muestre una retención que el guardado rechazaría.
        await OrderWithholdingEdits.ApplyAsync(
            order, query,
            new OrderWithholdingEditsContext(
                companyLookup, withholdingLookup, geographyLookup, tenantClock, query.TenantId, shippingResolver),
            updatedBy, now, cancellationToken);

        // Mismo orden que el guardado: el envío antes de recalcular el estado de pago.
        var shipping = keepsProjectLine
            ? null
            : await shippingResolver.ResolveAsync(
                query.TenantId, query.Shipping, order.ShippingDetails, order.IsStorePickup, order.Currency,
                order.ChargesVat, "order.order", cancellationToken);
        if (shipping is not null && shipping != order.ShippingDetails)
        {
            order.SetShipping(shipping, updatedBy, now);
        }

        // Mismo lugar que el guardado (spec retenciones por UVT §5.4): la vista previa muestra la UVT que el guardado dejaría.
        await TaxUnitRefresh.RefreshAsync(
            order, withholdingLookup, TaxUnitRefresh.DocumentDate(tenantClock, query.TenantId, order.CreatedAt),
            cancellationToken);

        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, query.TenantId, order, now, cancellationToken);
        // Sin historial: el preview no guarda nada.
        order.RecalculatePaymentStatus(previousNetTotal, now);

        var quotation = order.QuotationId is { } quotationId
            ? await quotationRepository.FindUntrackedAsync(query.TenantId, quotationId, cancellationToken)
            : null;
        // La versión que viaja es la guardada: el preview no es una edición y el If-Match del
        // guardado tiene que seguir valiendo.
        return new OrderDetailDto(
            order.ToDto() with { Version = storedVersion }, quotation?.QuotationNumber, [], quotation?.CreatedAt);
    }
}
