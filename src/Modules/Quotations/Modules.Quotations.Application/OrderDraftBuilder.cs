using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Lo que un pedido nuevo trae del cuerpo (manual) o de la integración (externo), antes de
/// preciar (spec 2026-10-15 §5.2 y §5.4).</summary>
public sealed record OrderDraftInput(
    Guid TenantId,
    Guid ClientId,
    MemberId AdvisorId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest BillingAccount,
    IReadOnlyList<OrderItemAddition> Items,
    OrderPaymentStatus PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofInput> Proofs,
    OrderSource Source,
    string? ExternalReference,
    bool AllowIncompleteCustomer);

/// <summary>
/// Arma un <see cref="Order"/> preciado como lo haría el cotizador: cliente elegible, cuenta de cobro
/// validada contra Companies, precio y descuento por escala de cada línea, foto del producto, descuentos
/// de grupo y mínimo de compra. No guarda nada: crear, previsualizar y el servicio de pedidos externos lo
/// comparten. Público porque el composition root lo registra.
/// </summary>
public sealed class OrderDraftBuilder(
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationSettingsStore settingsStore)
{
    public async Task<Order> BuildAsync(
        OrderDraftInput input, string orderNumber, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var customer = await customerLookup.FindAsync(input.TenantId, input.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, input.TenantId, input.ClientId, input.AllowIncompleteCustomer);

        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, input.TenantId, input.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing account.");
        var currency = billingAccount.Currency;

        // Línea por línea, como el alta de una línea en la cotización: un producto ausente, de otro
        // tenant o inactivo se rechaza con el código de siempre (ResolveManyAsync deja pasar el
        // inactivo a propósito, porque sirve al cambio de moneda de líneas que ya existen).
        var products = await productLookup.FindManyAsync(
            input.TenantId, input.Items.Select(item => item.ProductId).Distinct().ToArray(), cancellationToken);
        var lines = new List<OrderLineInput>(input.Items.Count);
        foreach (var item in input.Items)
        {
            var priced = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, input.TenantId, item.ProductId, item.Quantity, currency, input.IsRetail, cancellationToken);
            products.TryGetValue(item.ProductId, out var product);
            lines.Add(new OrderLineInput(
                item.ProductId,
                item.Quantity,
                priced.Pricing.UnitPrice,
                priced.Pricing.DiscountPercentage,
                priced.Pricing.TaxPercentage,
                product is null ? null : new QuotationProductSnapshot(product.Code, product.Name)));
        }

        var header = new OrderHeader(
            input.ClientId,
            input.AdvisorId,
            currency,
            input.PaymentMethod,
            input.IsRetail,
            input.GlobalScaleFloor,
            customer.WithRetention,
            customer.VatSurplus);
        var parties = input.Parties.ToDomain();
        var order = input.Source == OrderSource.Manual
            ? Order.CreateManual(
                OrderId.New(), input.TenantId, orderNumber, header, parties, billingAccount, lines,
                input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now)
            : Order.CreateExternal(
                OrderId.New(), input.TenantId, orderNumber, header, parties, billingAccount, lines,
                input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now, input.Source,
                input.ExternalReference!);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, input.TenantId, order, now, cancellationToken);
        return order;
    }
}
