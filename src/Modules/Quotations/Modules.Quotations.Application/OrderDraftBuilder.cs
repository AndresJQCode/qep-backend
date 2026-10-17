using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Lo que un pedido manual trae del cuerpo, antes de preciar (spec 2026-10-15 §5.2). El pedido
/// de una integración no pasa por acá: lo arma <see cref="ExternalOrderService"/> con el precio que cobró
/// la plataforma.</summary>
public sealed record OrderDraftInput(
    Guid TenantId,
    Guid ClientId,
    MemberId AdvisorId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderItemAddition> Items,
    OrderPaymentStatus PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofInput> Proofs,
    bool AllowIncompleteCustomer,
    ShippingRequest? Shipping = null);

/// <summary>
/// Arma un <see cref="Order"/> manual preciado como lo haría el cotizador: cliente elegible, cuenta de cobro
/// validada contra Companies, precio y descuento por escala de cada línea, foto del producto, descuentos
/// de grupo y mínimo de compra. No guarda nada: crear (<see cref="BuildAsync"/>) y previsualizar
/// (<see cref="PreviewAsync"/>) lo comparten. Público porque el composition root lo registra.
/// </summary>
public sealed class OrderDraftBuilder(
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationSettingsStore settingsStore,
    ShippingResolver shippingResolver,
    ITenantDefaultCurrency tenantDefaultCurrency)
{
    // El preview no tiene número: el handler lo borra antes de responder.
    private const string PreviewOrderNumber = "PREVIEW";

    /// <summary>El pedido que se va a guardar: sin cuenta de cobro no hay pedido.</summary>
    public async Task<Order> BuildAsync(
        OrderDraftInput input, string orderNumber, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (customer, resolved) = await ResolveCustomerAndAccountAsync(input, cancellationToken);
        var account = resolved
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing account.");
        var (header, parties, lines) = await PriceAsync(input, customer, account.Currency, cancellationToken);
        var shipping = await ResolveShippingAsync(input, parties, account.Currency, cancellationToken);
        var order = Order.CreateManual(
            OrderId.New(), input.TenantId, orderNumber, header, parties, account,
            shipping, lines,
            input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, input.TenantId, order, now, cancellationToken);
        return order;
    }

    /// <summary>El cálculo previo: la cuenta es opcional y, sin ella, se precia en la moneda por defecto del
    /// tenant. El resultado no se guarda.</summary>
    public async Task<Order> PreviewAsync(OrderDraftInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (customer, account) = await ResolveCustomerAndAccountAsync(input, cancellationToken);

        // Sin cuenta de cobro, el cálculo previo usa la moneda por defecto del tenant, el mismo precedente que
        // la cotización (CreateQuotation.cs): la pantalla de «Nuevo pedido» pinta el precio de la línea antes
        // de que se elija la empresa que factura. Con cuenta, manda la suya, como siempre.
        var currency = account?.Currency
            ?? Currencies.Normalize(await tenantDefaultCurrency.GetAsync(input.TenantId, cancellationToken));
        var (header, parties, lines) = await PriceAsync(input, customer, currency, cancellationToken);
        var shipping = await ResolveShippingAsync(input, parties, currency, cancellationToken);
        var order = Order.Preview(
            OrderId.New(), input.TenantId, PreviewOrderNumber, header, parties, account,
            shipping, lines,
            input.PaymentStatus, input.Notes, input.AdvisorId, input.Proofs, now);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, input.TenantId, order, now, cancellationToken);
        return order;
    }

    // El pedido todavía no existe: la recogida y la moneda salen del borrador, las mismas con las que el
    // agregado va a validar el envío. Sin cuerpo (o con recogida) el resolver devuelve null: sin envío.
    private async Task<ShippingDetails> ResolveShippingAsync(
        OrderDraftInput input, QuotationParties parties, string currency, CancellationToken cancellationToken) =>
        await shippingResolver.ResolveAsync(
            input.TenantId, input.Shipping, ShippingDetails.None, parties.IsStorePickup, currency, "order.order",
            cancellationToken)
        ?? ShippingDetails.None;

    // El cliente se valida antes que la cuenta, el mismo orden de errores que antes de separar guardar y
    // previsualizar.
    private async Task<(QuotationCustomerRef Customer, QuotationBillingAccount? Account)> ResolveCustomerAndAccountAsync(
        OrderDraftInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var customer = await customerLookup.FindAsync(input.TenantId, input.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, input.TenantId, input.ClientId, input.AllowIncompleteCustomer);

        var account = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, input.TenantId, input.BillingAccount, cancellationToken);
        return (customer, account);
    }

    private async Task<(OrderHeader Header, QuotationParties Parties, List<OrderLineInput> Lines)> PriceAsync(
        OrderDraftInput input, QuotationCustomerRef customer, string currency, CancellationToken cancellationToken)
    {
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
        return (header, input.Parties.ToDomain(), lines);
    }
}
