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
    OrderBillingEditRequest? Billing = null) : IQuery<OrderDetailDto>, IOrderEdits;

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
    IQuotationCustomerLookup customerLookup,
    ShippingResolver shippingResolver,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewOrderEditsQuery> validator)
    : IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        PreviewOrderEditsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        // Sin tracking: el preview muta el agregado en memoria y nada de eso puede llegar a un
        // SaveChangesAsync del mismo scope.
        var order = await orderRepository.FindUntrackedByIdAsync(
            query.TenantId, new OrderId(query.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);
        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        var storedVersion = order.Version;
        var customer = await customerLookup.FindAsync(query.TenantId, order.ClientId, cancellationToken);
        if (customer is not null)
        {
            order.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        await OrderItemEdits.ApplyAsync(
            order, query.Items, pricingLookup, query.TenantId, updatedBy, now, query.IsRetail, cancellationToken);
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
        if (order.IsRetail != query.IsRetail)
        {
            order.SetIsRetail(query.IsRetail, updatedBy, now);
        }

        if (order.GlobalScaleFloor != query.GlobalScaleFloor)
        {
            order.SetGlobalScaleFloor(query.GlobalScaleFloor, updatedBy, now);
        }

        // Mismo orden que el guardado: la facturación mueve la retención y el excedente de IVA, así que va antes
        // de recalcular. Sin la regla NIT por país: el preview no la aplica mientras la persona teclea.
        OrderBillingEdits.Apply(
            order, query.Billing, tenantCountry: null, enforceFiscalRules: false, updatedBy, now);

        // Mismo orden que el guardado: el envío antes de recalcular el estado de pago.
        var shipping = await shippingResolver.ResolveAsync(
            query.TenantId, query.Shipping, order.ShippingDetails, order.IsStorePickup, order.Currency,
            "order.order", cancellationToken);
        if (shipping is not null && shipping != order.ShippingDetails)
        {
            order.SetShipping(shipping, updatedBy, now);
        }

        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, query.TenantId, order, now, cancellationToken);
        order.RecalculatePaymentStatus(now);

        var quotationNumber = order.QuotationId is { } quotationId
            ? (await quotationRepository.FindUntrackedAsync(query.TenantId, quotationId, cancellationToken))?.QuotationNumber
            : null;
        // La versión que viaja es la guardada: el preview no es una edición y el If-Match del
        // guardado tiene que seguir valiendo.
        return new OrderDetailDto(order.ToDto() with { Version = storedVersion }, quotationNumber, []);
    }
}
