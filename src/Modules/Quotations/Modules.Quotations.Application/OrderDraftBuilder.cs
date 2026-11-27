using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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
    IReadOnlyCollection<OrderPaymentProofInput> Proofs,
    bool AllowIncompleteCustomer,
    ShippingRequest? Shipping = null,
    // Preview only (spec 2026-11-16 withholdings by company): the company chosen before its bank account.
    Guid? BillingCompanyId = null,
    // Spec 2026-11-23: los regalos, aparte de Items (que son las líneas pagadas); null es «sin regalos».
    IReadOnlyList<OrderItemAddition>? Gifts = null);

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
    ITenantPriceRounding priceRounding,
    ShippingResolver shippingResolver,
    ITenantDefaultCurrency tenantDefaultCurrency,
    ITenantDirectory tenantDirectory,
    IQuotationWithholdingLookup withholdingLookup,
    ITenantClock tenantClock,
    IQuotationGeographyLookup geographyLookup)
{
    // El preview no tiene número: el handler lo borra antes de responder.
    private const string PreviewOrderNumber = "PREVIEW";

    /// <summary>El pedido que se va a guardar: sin empresa emisora no hay pedido. La cuenta bancaria puede faltar (spec
    /// 2026-11-17, R3); entonces el pedido va en la moneda por defecto del tenant.</summary>
    public async Task<Order> BuildAsync(
        OrderDraftInput input, string orderNumber, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var defaultCurrency = Currencies.Normalize(await tenantDefaultCurrency.GetAsync(input.TenantId, cancellationToken));
        var (customer, resolved) = await ResolveCustomerAndAccountAsync(input, defaultCurrency, preview: false, cancellationToken);
        var account = resolved
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing company.");
        var orderDate = await LocalDateAsync(input.TenantId, now, cancellationToken);
        var (header, parties, lines) = await PriceAsync(
            input, customer, account, account.Currency, orderDate, cancellationToken);
        // Sólo al guardar: el cálculo previo corre mientras la persona escribe (spec 2026-10-26).
        BillingPartyFiscalRules.EnsureConsistent(parties.Billing);
        await PartyRegionRules.EnsureAsync(geographyLookup, parties, cancellationToken);
        var shipping = await ResolveShippingAsync(input, parties, account.Currency, account.ChargesVat, cancellationToken);
        var order = Order.CreateManual(
            OrderId.New(), input.TenantId, orderNumber, header, parties, account,
            shipping, lines,
            input.Notes, input.AdvisorId, input.Proofs, now, orderDate);
        await BillingPersonTypeRequirement.EnsureForOrderAsync(
            tenantDirectory, input.TenantId, order, cancellationToken);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, input.TenantId, order, now, cancellationToken);
        // Los descuentos de grupo movieron el neto: el pago se compara contra el definitivo.
        order.RecalculateDraftPaymentStatus();
        return order;
    }

    /// <summary>El cálculo previo: la cuenta es opcional y, sin ella, se precia en la moneda por defecto del
    /// tenant. El resultado no se guarda.</summary>
    public async Task<Order> PreviewAsync(OrderDraftInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Sin cuenta de cobro, el cálculo previo usa la moneda por defecto del tenant, el mismo precedente que
        // la cotización (CreateQuotation.cs): la pantalla de «Nuevo pedido» pinta el precio de la línea antes
        // de que se elija la empresa que factura. Con cuenta, manda la suya, como siempre. Se lee antes de la cuenta
        // porque la empresa sola (spec 2026-11-17, R3) arma la cuenta sin banco en esa moneda.
        var defaultCurrency = Currencies.Normalize(await tenantDefaultCurrency.GetAsync(input.TenantId, cancellationToken));
        var (customer, account) = await ResolveCustomerAndAccountAsync(input, defaultCurrency, preview: true, cancellationToken);
        var currency = account?.Currency ?? defaultCurrency;
        var orderDate = await LocalDateAsync(input.TenantId, now, cancellationToken);
        var (header, parties, lines) = await PriceAsync(input, customer, account, currency, orderDate, cancellationToken);
        var shipping = await ResolveShippingAsync(
            input, parties, currency, account?.ChargesVat ?? true, cancellationToken);
        var order = Order.Preview(
            OrderId.New(), input.TenantId, PreviewOrderNumber, header, parties, account,
            shipping, lines,
            input.Notes, input.AdvisorId, input.Proofs, now, orderDate);
        await OrderPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, input.TenantId, order, now, cancellationToken);
        order.RecalculateDraftPaymentStatus();
        return order;
    }

    // Spec 2026-11-16 §3: el día local de la creación es a la vez la «Fecha del pedido» y el día con que se resuelven las
    // retenciones del cliente, así que se lee el calendario una sola vez.
    private async Task<DateOnly> LocalDateAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        DocumentDates.LocalDate(await tenantClock.GetAsync(tenantId, cancellationToken), now);

    // El pedido todavía no existe: la recogida y la moneda salen del borrador, las mismas con las que el
    // agregado va a validar el envío. Sin cuerpo (o con recogida) el resolver devuelve null: sin envío.
    private async Task<ShippingDetails> ResolveShippingAsync(
        OrderDraftInput input, QuotationParties parties, string currency, bool chargesVat,
        CancellationToken cancellationToken) =>
        await shippingResolver.ResolveAsync(
            input.TenantId, input.Shipping, ShippingDetails.None, parties.IsStorePickup, currency, chargesVat,
            "order.order", cancellationToken)
        ?? ShippingDetails.None;

    // El cliente se valida antes que la cuenta, el mismo orden de errores que antes de separar guardar y
    // previsualizar. Sólo la vista previa acepta la empresa suelta (BillingCompanyId); guardar la pide en la cuenta.
    private async Task<(QuotationCustomerRef Customer, QuotationBillingAccount? Account)> ResolveCustomerAndAccountAsync(
        OrderDraftInput input, string currencyWithoutAccount, bool preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var customer = await customerLookup.FindAsync(input.TenantId, input.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, input.TenantId, input.ClientId, input.AllowIncompleteCustomer);

        var account = preview
            ? await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, input.TenantId, input.BillingAccount, input.BillingCompanyId, currencyWithoutAccount,
                cancellationToken)
            : await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, input.TenantId, input.BillingAccount, currencyWithoutAccount, cancellationToken);
        return (customer, account);
    }

    // Las tasas, con las del cliente del país de la empresa (spec impuestos por tipo §7.5). Sin cuenta, el cálculo previo no
    // tiene país y no retiene, igual que el pedido que se guardaría sin empresa.
    private async Task<(OrderHeader Header, QuotationParties Parties, List<OrderLineInput> Lines)> PriceAsync(
        OrderDraftInput input,
        QuotationCustomerRef customer,
        QuotationBillingAccount? account,
        string currency,
        DateOnly orderDate,
        CancellationToken cancellationToken)
    {
        var lines = await PriceLinesAsync(input.TenantId, input.Items, currency, input.IsRetail, cancellationToken);
        // Los regalos van después de las pagadas: así las posiciones de las pagadas no dependen de ellos.
        lines.AddRange(await PriceGiftLinesAsync(input.TenantId, input.Gifts, currency, cancellationToken));
        var withholdings = await OrderCustomerWithholdings.ResolveAsync(
            withholdingLookup,
            input.TenantId,
            input.ClientId,
            account,
            orderDate,
            cancellationToken);
        var header = new OrderHeader(
            input.ClientId,
            input.AdvisorId,
            currency,
            input.PaymentMethod,
            input.IsRetail,
            input.GlobalScaleFloor,
            customer.VatSurplus,
            withholdings);
        return (header, input.Parties.ToDomain(), lines);
    }

    /// <summary>Los regalos del cuerpo (spec 2026-11-23), preciados a precio de lista: <c>isRetail: true</c> salta las
    /// escalas a propósito, porque el regalo va a precio de lista y su descuento es el fijo del 100 %. Aparte de
    /// <see cref="PriceLinesAsync"/>, que también usa la tienda pública.</summary>
    private async Task<List<OrderLineInput>> PriceGiftLinesAsync(
        Guid tenantId, IReadOnlyList<OrderItemAddition>? gifts, string currency, CancellationToken cancellationToken)
    {
        if (gifts is null or { Count: 0 })
        {
            return [];
        }

        var pricedVariants = await QuotationProductPricingResolver.FindVariantsAsync(
            pricingLookup, tenantId, gifts.Select(gift => gift.VariantId), cancellationToken);
        var products = await productLookup.FindManyAsync(
            tenantId, gifts.Select(gift => gift.VariantId).Distinct().ToArray(), cancellationToken);
        var lines = new List<OrderLineInput>(gifts.Count);
        foreach (var gift in gifts)
        {
            var priced = QuotationProductPricingResolver.Resolve(
                pricedVariants.GetValueOrDefault(gift.VariantId), tenantId, gift.VariantId, gift.Quantity,
                currency, isRetail: true);
            products.TryGetValue(gift.VariantId, out var product);
            lines.Add(new OrderLineInput(
                gift.VariantId,
                priced.ProductId,
                gift.Quantity,
                priced.Pricing.UnitPrice,
                priced.Pricing.DiscountPercentage,
                priced.Pricing.TaxPercentage,
                product is null ? null : new QuotationProductSnapshot(product.Code, product.Name),
                Kind: priced.Kind,
                SalesUnit: priced.SalesUnit,
                IsGift: true));
        }

        return lines;
    }

    /// <summary>Las líneas preciadas con el catálogo del tenant, sin cliente ni cuenta: lo comparten el pedido
    /// manual y la tienda pública (<see cref="StoreOrderGateway"/>), que precia antes de saber a quién se vende.</summary>
    internal async Task<List<OrderLineInput>> PriceLinesAsync(
        Guid tenantId, IReadOnlyList<OrderItemAddition> items, string currency, bool isRetail,
        CancellationToken cancellationToken, QuotationPriceChannel channel = QuotationPriceChannel.Quotations)
    {
        // Una lectura del catálogo con precio para todo el pedido (el preview corre mientras se escribe)
        // y la comprobación línea por línea, como el alta de una línea en la cotización: un producto
        // ausente, de otro tenant o inactivo se rechaza con el código de siempre (ResolveManyAsync deja
        // pasar el inactivo a propósito, porque sirve al cambio de moneda de líneas que ya existen).
        var pricedVariants = await QuotationProductPricingResolver.FindVariantsAsync(
            pricingLookup, tenantId, items.Select(item => item.VariantId), cancellationToken, channel);
        var products = await productLookup.FindManyAsync(
            tenantId, items.Select(item => item.VariantId).Distinct().ToArray(), cancellationToken);
        var lines = new List<OrderLineInput>(items.Count);
        foreach (var item in items)
        {
            var priced = QuotationProductPricingResolver.Resolve(
                pricedVariants.GetValueOrDefault(item.VariantId), tenantId, item.VariantId, item.Quantity,
                currency, isRetail);
            products.TryGetValue(item.VariantId, out var product);
            lines.Add(new OrderLineInput(
                item.VariantId,
                priced.ProductId,
                item.Quantity,
                priced.Pricing.UnitPrice,
                priced.Pricing.DiscountPercentage,
                priced.Pricing.TaxPercentage,
                product is null ? null : new QuotationProductSnapshot(product.Code, product.Name),
                // Tipo y unidad del catálogo (spec 2026-11-18 §3.4), también para la tienda: es lo honesto.
                Kind: priced.Kind,
                SalesUnit: priced.SalesUnit));
        }

        return lines;
    }
}
