using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma la respuesta HTTP del detalle de un pedido (spec 2026-10-15 §6.2): resuelve cliente, asesor,
/// empresa de la cuenta de cobro, productos de las líneas y nombres de quienes figuran en el
/// historial, con los mismos lookups que el compositor de cotizaciones.
/// </summary>
public interface IOrderResponseComposer
{
    Task<OrderDetailResponse> ComposeAsync(Guid tenantId, OrderDetailDto detail, CancellationToken cancellationToken);

    /// <summary>Sólo el pedido, para las rutas que no responden el detalle (/approve, /cancel…). Pasa por acá y no por
    /// <see cref="OrderResponses.ToResponse"/> directo porque el desglose de retenciones necesita el calendario del tenant
    /// (spec retenciones por UVT §5.2).</summary>
    Task<OrderResponse> ComposeOrderAsync(Guid tenantId, OrderDto order, CancellationToken cancellationToken);
}

public sealed class OrderResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationWithholdingLookup withholdingLookup,
    IQuotationGeographyLookup geographyLookup,
    IQuotationBuyerAccountLookup buyerAccountLookup,
    ITenantClock tenantClock)
    : IOrderResponseComposer
{
    public async Task<OrderDetailResponse> ComposeAsync(
        Guid tenantId, OrderDetailDto detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var order = detail.Order;
        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        var memberIds = detail.History
            .Where(entry => entry.MemberId is not null)
            .Select(entry => entry.MemberId!.Value)
            .Append(order.AdvisorId)
            .Distinct()
            .ToArray();
        var members = await advisorLookup.FindAsync(tenantId, memberIds, cancellationToken);
        var advisor = members.GetValueOrDefault(order.AdvisorId);
        // Sin cuenta sólo llega el cálculo previo, que precia antes de que se elija la empresa que factura.
        var company = order.BillingAccount is { } billing
            ? await companyLookup.FindAsync(tenantId, billing.CompanyId, cancellationToken)
            : null;
        var products = await productLookup.FindManyAsync(
            tenantId, order.Items.Select(item => item.VariantId).Distinct().ToArray(), cancellationToken);
        var clientContacts = await QuotationResponseComposer.ClientContactsAsync(
            customerLookup, tenantId, customer, cancellationToken);
        // El pedido tiene una sola fuente (sus tasas, en BillingWithholdingTaxRates) y aplica la regla del cliente: el
        // agente se consulta siempre, también con parte propia (spec ReteICA por empresa §9.4, R11).
        var municipal = await MunicipalWithholdingExplanation.ExplainAsync(
            withholdingLookup,
            geographyLookup,
            tenantId,
            order.ClientId,
            order.BillingAccount,
            order.OperationCityId,
            ownBilling: false,
            order.BillingWithholdingTaxRates,
            MunicipalWithholdingExplanation.DateOf(order.CreatedAt),
            cancellationToken);
        // Spec 2026-11-15 §4: una cuenta borrada deja el id en el pedido; el detalle la muestra como «sin cuenta».
        var buyerAccount = order.BuyerAccountId is { } buyerAccountId
            ? await buyerAccountLookup.FindAsync(buyerAccountId, cancellationToken)
            : null;

        return new OrderDetailResponse(
            await ComposeOrderAsync(tenantId, order, cancellationToken),
            QuotationResponseComposer.ToClientResponse(customer),
            advisor?.DisplayName,
            advisor?.Email,
            QuotationResponseComposer.ToBillingResponse(order.BillingAccount, company),
            order.Items
                .Select(item => QuotationResponseComposer.ToItemResponse(item, products, order.GlobalScaleFloor))
                .ToArray(),
            order.QuotationId is { } quotationId && detail.QuotationNumber is { } number
                ? new SourceQuotationResponse(quotationId, number)
                : null,
            detail.History
                .Select(entry => new OrderHistoryEntryResponse(
                    entry.Id,
                    entry.EventType,
                    entry.EventAt,
                    entry.MemberId,
                    entry.MemberId is { } member ? members.GetValueOrDefault(member)?.Label : null,
                    entry.Details))
                .ToArray(),
            QuotationResponseComposer.AvailableFloors(order.Items, products),
            clientContacts,
            municipal.OperationCity,
            municipal.BillingCompanyCity,
            municipal.Entries,
            buyerAccount is null ? null : new BuyerAccountResponse(buyerAccount.Id, buyerAccount.Email));
    }

    public async Task<OrderResponse> ComposeOrderAsync(Guid tenantId, OrderDto order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var documentYear = await QuotationResponseComposer.DocumentYearAsync(
            tenantClock, tenantId, order.CreatedAt, order.WithholdingBreakdown, cancellationToken);
        return OrderResponses.ToResponse(order, documentYear);
    }
}

/// <summary>El <see cref="OrderResponse"/> de un <see cref="OrderDto"/>: lo usan los endpoints y el
/// compositor del detalle.</summary>
public static class OrderResponses
{
    /// <param name="documentYear">El año local del pedido, para decir si la UVT del desglose está desactualizada; null si
    /// ninguna fila trae año. Lo resuelve <see cref="IOrderResponseComposer.ComposeOrderAsync"/> con el calendario del
    /// tenant.</param>
    public static OrderResponse ToResponse(OrderDto order, int? documentYear)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderResponse(
            order.Id,
            order.OrderNumber,
            order.Source,
            order.QuotationId,
            order.ExternalReference,
            order.ClientId,
            order.AdvisorId,
            order.Status,
            order.PaymentStatus,
            order.FulfillmentStatus,
            order.Invoiced,
            order.PaymentMethod,
            order.Currency,
            order.IsRetail,
            order.GlobalScaleFloor,
            order.BillingUsesBusinessName,
            order.IsStorePickup,
            order.BillsToFinalConsumer,
            order.BillingWithRetention,
            order.BillingVatSurplus,
            order.CustomerVatSurplus,
            order.Subtotal,
            order.TaxPercentage,
            order.TaxAmount,
            order.DiscountAmount,
            order.Total,
            order.RetentionAmount,
            order.NetTotal,
            order.Notes,
            order.BillingAccount,
            order.Parties
                .Select(party => new QuotationPartyResponse(
                    party.Id, party.Role, party.Name, party.IdentificationNumber, party.Phone, party.Email,
                    party.Address, party.DepartmentId, party.CityId, party.PersonType, party.IdentificationType,
                    party.Country, party.CityName))
                .ToArray(),
            order.ConvertedAt,
            order.ConvertedBy,
            order.PaidMarkedAt,
            order.PaidMarkedBy,
            order.CancelledAt,
            order.CancelledBy,
            order.CancellationReason,
            order.InvoicedAt,
            order.InvoicedBy,
            order.RitualCollectionSyncId,
            order.CreatedAt,
            order.UpdatedAt,
            order.Version,
            order.PaymentProofs
                .Select(proof => new OrderPaymentProofResponse(
                    proof.Id, proof.FileId, proof.Amount, proof.UploadedAt, proof.PaidOn))
                .ToArray(),
            OrderShippingResponse.From(order.Shipping, order.ExternalShippingTitle),
            order.FreightSubtotal,
            order.FreightTaxAmount,
            [.. order.Withholdings.Select(QuotationResponseComposer.ToWithholdingResponse)],
            [.. order.CustomerWithholdingTaxRates.Select(QuotationResponseComposer.ToWithholdingTaxRateResponse)],
            [.. order.BillingWithholdingTaxRates.Select(QuotationResponseComposer.ToWithholdingTaxRateResponse)],
            order.PreparedAt,
            order.PreparedBy,
            order.ArchivedAt,
            order.ArchivedBy,
            order.Balance,
            order.BuyerNotes,
            QuotationResponseComposer.ToBreakdownResponse(order.WithholdingBreakdown, documentYear));
    }
}
