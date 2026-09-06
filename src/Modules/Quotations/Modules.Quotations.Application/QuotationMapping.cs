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
        quotation.Currency.ToCode(),
        quotation.Subtotal,
        quotation.TaxPercentage,
        quotation.TaxAmount,
        quotation.DiscountAmount,
        quotation.Total,
        quotation.CustomerVatSurplus,
        quotation.RetentionAmount,
        quotation.NetTotal,
        quotation.Notes,
        quotation.Parties.Select(ToDto).ToArray(),
        quotation.BillingUsesBusinessName,
        ToDto(quotation.BillingAccount),
        quotation.CreatedBy.Value,
        quotation.UpdatedBy?.Value,
        quotation.UpdatedAt,
        quotation.SentAt,
        quotation.PdfFileId,
        quotation.Items.Select(ToDto).ToArray());

    private static QuotationBillingAccountDto? ToDto(QuotationBillingAccount? account) =>
        account is null
            ? null
            : new QuotationBillingAccountDto(
                account.CompanyId, account.BankName, account.AccountNumber, account.Currency);

    private static QuotationPartyDto ToDto(QuotationParty party) => new(
        party.Id.Value,
        party.Role.ToString(),
        party.Name,
        party.Phone,
        party.Email,
        party.Address,
        party.DepartmentId,
        party.CityId);

    public static QuotationListItemDto ToListItemDto(
        this Quotation quotation, string? clientName, string? advisorEmail) => new(
        quotation.Id.Value,
        quotation.QuotationNumber,
        quotation.ClientId,
        clientName,
        quotation.AdvisorId.Value,
        advisorEmail,
        quotation.Status.ToString(),
        quotation.CreatedAt,
        quotation.Currency.ToCode(),
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

    public static QuotationParties ToDomain(this QuotationPartiesRequest? request) =>
        request is null
            ? QuotationParties.Empty
            : new QuotationParties(
                request.Billing.ToDomain(),
                request.Shipping.ToDomain(),
                request.BillingUsesBusinessName);

    private static QuotationPartyDetails? ToDomain(this QuotationPartyRequest? request) =>
        request is null
            ? null
            : new QuotationPartyDetails
            {
                Name = request.Name,
                Phone = request.Phone,
                Email = request.Email,
                Address = request.Address,
                DepartmentId = request.DepartmentId,
                CityId = request.CityId
            };
}
