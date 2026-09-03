using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal static class QuotationMapping
{
    public static QuotationDto ToDto(this Quotation quotation) => new(
        quotation.Id.Value,
        quotation.QuotationNumber,
        quotation.ClientId,
        quotation.AdvisorId.Value,
        quotation.Status.ToString(),
        quotation.CreatedAt,
        quotation.ValidUntil,
        quotation.PaymentMethod,
        quotation.Subtotal,
        quotation.TaxPercentage,
        quotation.TaxAmount,
        quotation.DiscountAmount,
        quotation.Total,
        quotation.CustomerVatSurplus,
        quotation.RetentionAmount,
        quotation.NetTotal,
        quotation.Notes,
        quotation.BillingNameOverride,
        quotation.BillingAddressOverride,
        quotation.DeliveryAddressOverride,
        quotation.DeliveryCityOverride,
        quotation.CreatedBy.Value,
        quotation.UpdatedBy?.Value,
        quotation.UpdatedAt,
        quotation.SentAt,
        quotation.PdfFileId,
        quotation.Items.Select(ToDto).ToArray());

    public static QuotationListItemDto ToListItemDto(this Quotation quotation) => new(
        quotation.Id.Value,
        quotation.QuotationNumber,
        quotation.ClientId,
        quotation.AdvisorId.Value,
        quotation.Status.ToString(),
        quotation.CreatedAt,
        quotation.Total);

    private static QuotationItemDto ToDto(QuotationItem item) => new(
        item.Id.Value,
        item.ProductId,
        item.Quantity,
        item.UnitPrice,
        item.DiscountPercentage,
        item.DiscountAmount,
        item.Subtotal,
        item.TaxPercentage,
        item.TaxAmount,
        item.Position);

    public static QuotationOverrides ToDomain(this QuotationOverridesRequest? request) =>
        request is null
            ? QuotationOverrides.Empty
            : new QuotationOverrides
            {
                BillingName = request.BillingName,
                BillingAddress = request.BillingAddress,
                DeliveryAddress = request.DeliveryAddress,
                DeliveryCity = request.DeliveryCity
            };
}
