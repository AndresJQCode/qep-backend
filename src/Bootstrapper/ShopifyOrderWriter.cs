using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-15 §6.2.3 y §6.3: Shopify → Quotations por <see cref="IExternalOrderWriter"/>, el único camino para crear un pedido
/// sin sesión HTTP (H4). Traduce los tipos propios de Shopify y los rechazos del pedido a resultados que el importador guarda en
/// el espejo.
/// <para><b>Cada llamada resuelve el escritor en un scope propio</b> (arrastre de la tarea 2): ante la carrera de la clave externa,
/// <c>ExternalOrderService</c> llama a <c>IQuotationsUnitOfWork.DiscardPendingChanges()</c>, que limpia todo el tracker de
/// <c>QuotationsDbContext</c>. En el scope del llamador ese tracker puede tener lo que haya leído o dejado pendiente otro
/// adaptador; en uno nuevo no hay nada que perder.</para>
/// </summary>
internal sealed class ShopifyOrderWriter(IServiceScopeFactory scopes) : IShopifyOrderWriter
{
    private const string AlreadyInvoiced = "order.order.already_invoiced";
    private const string AlreadyCancelled = "order.order.already_cancelled";

    // Lo lanza QuotationsUnitOfWork cuando otra alta tomó el mismo consecutivo a la vez: transitorio, se reintenta.
    private const string NumberTaken = "order.order.number_taken";

    // §6.3: un reembolso o una anulación del pago en Shopify no cambian el pedido de QEP.
    private static readonly HashSet<string> NotSynced = new(StringComparer.Ordinal) { "refunded", "partially_refunded", "voided" };

    public async Task<ShopifyOrderWriteResult> CreateAsync(ShopifyOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var external = ToExternal(draft);
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        try
        {
            var result = await writer.CreateAsync(external, cancellationToken);
            return ShopifyOrderWriteResult.Written(result.OrderId, result.OrderNumber, result.Total, result.AlreadyExisted, result.ClientId);
        }
        catch (QuotationsDomainException error) when (error.Code != NumberTaken)
        {
            return ShopifyOrderWriteResult.Rejected(error.Code);
        }
        catch (ResourceNotFoundException error)
        {
            // La empresa de la cuenta de cobro ya no existe (quotation.billing.company_not_found): es una regla del pedido, no un
            // fallo transitorio, y reintentar no lo arregla.
            return ShopifyOrderWriteResult.Rejected(error.Code);
        }
    }

    public async Task<ShopifyOrderOutcome> SyncPaymentStatusAsync(
        Guid tenantId, Guid orderId, string financialStatus, CancellationToken cancellationToken)
    {
        var normalized = Normalize(financialStatus);
        if (NotSynced.Contains(normalized))
        {
            return ShopifyOrderOutcome.Unchanged;
        }

        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        try
        {
            await writer.SyncPaymentStatusAsync(tenantId, orderId, ToPaymentStatus(normalized), cancellationToken);
            return ShopifyOrderOutcome.Applied;
        }
        catch (QuotationsDomainException error)
        {
            // order.order.not_pending y order.order.payment_has_proofs (H5): después de aprobar, o con comprobantes propios, el
            // pago lo lleva QEP. Va a last_error del espejo y no se reintenta.
            return ShopifyOrderOutcome.Ignored(error.Code);
        }
        catch (ResourceNotFoundException error)
        {
            return ShopifyOrderOutcome.Ignored(error.Code);
        }
    }

    public async Task<ShopifyOrderOutcome> CancelAsync(
        Guid tenantId, Guid orderId, Guid byMembershipId, string reason, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        try
        {
            await writer.CancelAsync(tenantId, orderId, byMembershipId, reason, cancellationToken);
            return ShopifyOrderOutcome.Applied;
        }
        catch (QuotationsDomainException error) when (error.Code == AlreadyInvoiced)
        {
            return ShopifyOrderOutcome.AlreadyInvoiced;
        }
        catch (QuotationsDomainException error) when (error.Code == AlreadyCancelled)
        {
            return ShopifyOrderOutcome.AlreadyCancelled;
        }
        catch (QuotationsDomainException error)
        {
            return ShopifyOrderOutcome.Ignored(error.Code);
        }
        catch (ResourceNotFoundException error)
        {
            return ShopifyOrderOutcome.Ignored(error.Code);
        }
    }

    public async Task<ShopifyOrderWriteResult?> FindByExternalKeyAsync(Guid tenantId, string externalKey, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var result = await writer.FindByExternalKeyAsync(tenantId, OrderSource.Shopify, externalKey, cancellationToken);
        return result is null
            ? null
            : ShopifyOrderWriteResult.Written(result.OrderId, result.OrderNumber, result.Total, alreadyExisted: true, result.ClientId);
    }

    private static ExternalOrderDraft ToExternal(ShopifyOrderDraft draft)
    {
        var shipping = draft.Shipping;
        var isStorePickup = shipping?.IsStorePickup == true;
        var cashOnDelivery = !isStorePickup && shipping?.CashOnDelivery == true;
        var party = isStorePickup || draft.ShippingParty is not { } shippingParty
            ? null
            : new QuotationPartyRequest(
                shippingParty.Name, null, shippingParty.Phone, shippingParty.Email, shippingParty.Address,
                shippingParty.DepartmentId, shippingParty.CityId);

        return new ExternalOrderDraft(
            draft.TenantId,
            OrderSource.Shopify,
            draft.ExternalReference,
            draft.ClientId,
            draft.AdvisorMembershipId,
            draft.PaymentMethod,
            draft.Notes,
            // La facturación va siempre a los datos del cliente (§6.2.3 paso 2).
            new QuotationPartiesRequest(Billing: null, Shipping: party, IsStorePickup: isStorePickup),
            new QuotationBillingAccountRequest(
                draft.BillingAccount.CompanyId, draft.BillingAccount.BankName, draft.BillingAccount.AccountNumber,
                draft.BillingAccount.Currency),
            draft.Lines
                .Select(line => new ExternalOrderLine(
                    line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage))
                .ToArray(),
            // H5: con contraentrega el dominio sólo admite PaymentPending; el importador sincroniza el pago después (D-S26).
            cashOnDelivery ? OrderPaymentStatus.PaymentPending : ToPaymentStatus(Normalize(draft.FinancialStatus)),
            draft.OccurredAt,
            // D-S28: con recogida, ningún envío (el dominio lo rechazaría con shipping_not_allowed_on_pickup).
            isStorePickup || shipping is null
                ? ShippingDetails.None
                : new ShippingDetails(
                    shipping.CarrierId, shipping.CarrierName, ToChargeMode(shipping.ChargeMode), shipping.FreightAmount,
                    shipping.FreightTaxPercentage, shipping.CashOnDelivery),
            draft.ExternalKey,
            draft.ExternalShippingTitle);
    }

    private static string Normalize(string? financialStatus) => financialStatus?.Trim().ToLowerInvariant() ?? string.Empty;

    // §6.2.3 paso 4: paid → pagado; partially_paid → abono; cualquier otro (pending, authorized, expired…) → pendiente.
    private static OrderPaymentStatus ToPaymentStatus(string financialStatus) => financialStatus switch
    {
        "paid" => OrderPaymentStatus.FullPaymentReceived,
        "partially_paid" => OrderPaymentStatus.PartialPaymentReceived,
        _ => OrderPaymentStatus.PaymentPending,
    };

    // Sólo Charged y Free por nombre (§6.2.2 paso 3): «al cobro» nunca sale de la tienda, y Enum.TryParse solo aceptaría
    // también "5" o "Charged,Free". Es un error de programación del importador, no un dato de la tienda.
    private static FreightChargeMode? ToChargeMode(string? chargeMode)
    {
        if (string.IsNullOrWhiteSpace(chargeMode))
        {
            return null;
        }

        var trimmed = chargeMode.Trim();
        if (string.Equals(trimmed, nameof(FreightChargeMode.Charged), StringComparison.OrdinalIgnoreCase))
        {
            return FreightChargeMode.Charged;
        }

        if (string.Equals(trimmed, nameof(FreightChargeMode.Free), StringComparison.OrdinalIgnoreCase))
        {
            return FreightChargeMode.Free;
        }

        throw new ArgumentOutOfRangeException(
            nameof(chargeMode), chargeMode, "A store order is either Charged or Free; OnDelivery never comes from the store.");
    }
}
