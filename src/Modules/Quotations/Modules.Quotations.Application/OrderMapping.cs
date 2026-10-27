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
        order.PaymentMethod,
        order.Currency,
        order.IsRetail,
        order.GlobalScaleFloor,
        order.BillingUsesBusinessName,
        order.IsStorePickup,
        order.BillsToFinalConsumer,
        order.PartyWithRetention,
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
            ? new QuotationBillingAccountDto(account.CompanyId, account.BankName, account.AccountNumber, account.Currency)
            : null,
        order.Parties.Select(ToDto).ToArray(),
        order.Items.OrderBy(item => item.Position).Select(ToDto).ToArray(),
        order.ConvertedAt,
        order.ConvertedBy.Value,
        order.ApprovedAt,
        order.ApprovedBy?.Value,
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
        order.ExternalShippingTitle);

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

    public static OrderListItemDto ToListItemDto(
        this Order order, string? quotationNumber, string? clientName, string? advisorName) => new(
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
        order.PaymentMethod,
        order.ConvertedAt,
        order.Currency,
        order.Total,
        order.Source.ToString(),
        order.ExternalReference,
        order.ShippingDtoOf());

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
        party.IdentificationType?.ToWireValue());

    private static QuotationItemDto ToDto(OrderItem item) => new(
        item.Id.Value,
        item.VariantId,
        item.ProductId,
        item.Quantity,
        item.UnitPrice,
        item.DiscountPercentage,
        item.DiscountAmount,
        item.DiscountedUnitPrice,
        item.Subtotal,
        item.TaxPercentage,
        item.TaxAmount,
        item.Position,
        item.DiscountOrigin.ToString(),
        item.ProductCode,
        item.ProductName);

    private static OrderPaymentProofDto ToDto(OrderPaymentProof proof) => new(
        proof.Id.Value, proof.FileId, proof.Amount, proof.UploadedAt, proof.PaidOn);
}
