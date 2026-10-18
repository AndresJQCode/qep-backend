namespace Modules.Quotations.Application;

/// <summary>
/// Arma la respuesta HTTP del detalle de un pedido (spec 2026-10-15 §6.2): resuelve cliente, asesor,
/// empresa de la cuenta de cobro, productos de las líneas y nombres de quienes figuran en el
/// historial, con los mismos lookups que el compositor de cotizaciones.
/// </summary>
public interface IOrderResponseComposer
{
    Task<OrderDetailResponse> ComposeAsync(Guid tenantId, OrderDetailDto detail, CancellationToken cancellationToken);
}

public sealed class OrderResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup)
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
            tenantId, order.Items.Select(item => item.ProductId).Distinct().ToArray(), cancellationToken);

        return new OrderDetailResponse(
            OrderResponses.ToResponse(order),
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
            QuotationResponseComposer.AvailableFloors(order.Items, products));
    }
}

/// <summary>El <see cref="OrderResponse"/> de un <see cref="OrderDto"/>: lo usan los endpoints y el
/// compositor del detalle.</summary>
public static class OrderResponses
{
    public static OrderResponse ToResponse(OrderDto order)
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
                    party.Address, party.DepartmentId, party.CityId))
                .ToArray(),
            order.ConvertedAt,
            order.ConvertedBy,
            order.ApprovedAt,
            order.ApprovedBy,
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
            order.FreightTaxAmount);
    }
}
