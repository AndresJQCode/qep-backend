using BuildingBlocks.Domain.Pricing;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal static class OrderMapping
{
    public static OrderDto ToDto(this Order order) => new(
        order.Id.Value,
        order.OrderNumber,
        order.Source.ToString(),
        order.QuotationId?.Value,
        order.ExternalReference,
        order.ClientId,
        order.AdvisorId.Value,
        order.Status.ToString(),
        order.PaymentStatus.ToString(),
        order.FulfillmentStatus.ToString(),
        order.IsInvoiced,
        order.PaymentMethod,
        order.Currency,
        order.IsRetail,
        order.GlobalScaleFloor,
        order.BillingUsesBusinessName,
        order.IsStorePickup,
        order.BillsToFinalConsumer,
        // Derivado para el frontend viejo (spec impuestos por tipo §9.3): con parte propia, billingWithholdingTaxRates no
        // vacía (las tasas vigentes, apliquen o no); sin ella, null. Nunca null con parte propia: el frontend viejo
        // bloquea con null (qep-frontend/src/features/quotes/utils/quote-actions.ts:104-106).
        order.Billing is null ? null : order.Withholdings.Count > 0,
        order.PartyVatSurplus,
        order.AppliesVatSurplus,
        order.Subtotal,
        order.TaxPercentage,
        order.TaxAmount,
        order.DiscountAmount,
        order.Total,
        order.RetentionAmount,
        order.NetTotal,
        order.Notes,
        order.BillingAccount is { } account
            ? new QuotationBillingAccountDto(
                account.CompanyId, account.BankName, account.AccountNumber, account.Currency, account.Country, account.CityId,
                account.ChargesVat)
            : null,
        order.Parties.Select(ToDto).ToArray(),
        order.Items.OrderBy(item => item.Position).Select(item => ToDto(item, order.PriceRounding)).ToArray(),
        order.ConvertedAt,
        order.ConvertedBy.Value,
        order.PaidMarkedAt,
        order.PaidMarkedBy?.Value,
        order.CancelledAt,
        order.CancelledBy?.Value,
        order.CancellationReason,
        order.InvoicedAt,
        order.InvoicedBy?.Value,
        order.RitualCollectionSyncId,
        order.CreatedAt,
        order.UpdatedAt,
        order.Version,
        order.PaymentProofs.Select(ToDto).ToArray(),
        order.ShippingDtoOf(),
        order.FreightSubtotal,
        order.FreightTaxAmount,
        order.ExternalShippingTitle,
        [.. order.AppliedWithholdings.Select(QuotationMapping.ToDto)],
        [],
        [.. order.Withholdings.OrderBy(withholding => withholding.Position).Select(QuotationMapping.ToRateDto)],
        order.PreparedAt,
        order.PreparedBy?.Value,
        order.ArchivedAt,
        order.ArchivedBy?.Value,
        order.Balance,
        order.OperationCityId,
        order.BuyerNotes,
        order.BuyerAccountId,
        [.. order.WithholdingVerdicts.Select(QuotationMapping.ToDto)],
        order.OrderDate,
        order.RequiresShipping,
        WholesaleOf(order),
        order.InvoiceProvider,
        order.InvoiceNumber,
        order.IsEditable);

    /// <summary>Mayorista fase 3: el vínculo del pedido con la orden del distribuidor; null fuera de un pedido mayorista.</summary>
    public static OrderWholesaleState? WholesaleOf(Order order) =>
        order is { Source: OrderSource.Wholesale, BuyerTenantId: { } buyerTenantId, PurchaseOrderId: { } purchaseOrderId }
            ? new OrderWholesaleState(
                buyerTenantId,
                purchaseOrderId,
                order.ExternalReference ?? string.Empty,
                ApprovalOf(order),
                order.WholesaleApprovedAt,
                order.WholesaleApprovedBy?.Value,
                order.CancelledByBuyer)
            : null;

    // D-1: la marca, no un estado. Un anulado sin aprobar sigue diciendo Pending; el estado del pedido dice que se anuló.
    private static string ApprovalOf(Order order) =>
        order.WholesaleApprovedAt is null ? WholesaleApprovals.Pending : WholesaleApprovals.Approved;

    /// <summary>La entrega por envío con forma fija, también en recogida: gemelo de
    /// <see cref="QuotationMapping.ShippingDtoOf"/>.</summary>
    public static ShippingDto ShippingDtoOf(this Order order) => new(
        order.IsStorePickup,
        order.CarrierId is { } carrierId
            ? new ShippingCarrierDto(carrierId, order.CarrierName ?? string.Empty)
            : null,
        order.FreightChargeMode?.ToString(),
        order.FreightAmount,
        order.FreightTaxPercentage,
        order.FreightSubtotal,
        order.FreightTaxAmount,
        order.CashOnDelivery);

    // requiresShipping llega resuelto por página: la búsqueda del listado no trae las líneas, así que order.Items acá
    // está vacía y order.RequiresShipping diría siempre true.
    public static OrderListItemDto ToListItemDto(
        this Order order, string? quotationNumber, string? clientName, string? advisorName, bool requiresShipping) => new(
        order.Id.Value,
        order.OrderNumber,
        order.QuotationId?.Value,
        quotationNumber,
        order.ClientId,
        clientName,
        order.AdvisorId.Value,
        advisorName,
        order.Status.ToString(),
        order.PaymentStatus.ToString(),
        order.FulfillmentStatus.ToString(),
        order.IsInvoiced,
        order.PaymentMethod,
        order.ConvertedAt,
        order.Currency,
        order.Total,
        order.Source.ToString(),
        order.ExternalReference,
        order.ShippingDtoOf(),
        order.OrderDate,
        requiresShipping,
        WholesaleOf(order) is { } wholesale ? new OrderListWholesaleDto(wholesale.PurchaseOrderNumber, wholesale.Approval) : null);

    public static OrderHistoryEntryDto ToDto(this OrderHistoryEntry entry) => new(
        entry.Id.Value,
        entry.EventType.ToString(),
        entry.EventAt,
        entry.MemberId?.Value,
        entry.Details);

    private static QuotationPartyDto ToDto(OrderParty party) => new(
        party.Id.Value,
        party.Role.ToString(),
        party.Name,
        party.IdentificationNumber,
        party.Phone,
        party.Email,
        party.Address,
        party.DepartmentId,
        party.CityId,
        party.PersonType?.ToWireValue(),
        party.IdentificationType?.ToWireValue(),
        party.Country ?? QuotationPartyDetails.ColombiaCountryCode,
        party.CityName,
        party.RegionId,
        party.PostalCode);

    private static QuotationItemDto ToDto(OrderItem item, PriceRounding rounding) => new(
        item.Id.Value,
        item.VariantId,
        item.ProductId,
        item.Quantity,
        item.UnitPrice,
        item.DiscountPercentage,
        item.DiscountAmount,
        item.DiscountedUnitPriceUnder(rounding),
        item.Subtotal,
        item.TaxPercentage,
        item.TaxAmount,
        item.Position,
        item.DiscountOrigin.ToString(),
        item.ProductCode,
        item.ProductName,
        item.Kind.ToString(),
        item.SalesUnit.ToString(),
        item.SourcePurchaseOrderLineId,
        item.IsGift);

    private static OrderPaymentProofDto ToDto(OrderPaymentProof proof) => new(
        proof.Id.Value, proof.FileId, proof.Amount, proof.UploadedAt, proof.PaidOn);
}
