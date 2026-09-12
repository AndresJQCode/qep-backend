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
        // El efectivo y no el snapshot: ver el comentario de QuotationDto.CustomerVatSurplus.
        quotation.AppliesVatSurplus,
        quotation.RetentionAmount,
        quotation.NetTotal,
        quotation.Notes,
        quotation.Parties.Select(ToDto).ToArray(),
        quotation.BillingUsesBusinessName,
        quotation.IsStorePickup,
        quotation.BillsToFinalConsumer,
        ToDto(quotation.BillingAccount),
        quotation.CreatedBy.Value,
        quotation.UpdatedBy?.Value,
        quotation.UpdatedAt,
        quotation.SentAt,
        quotation.PdfFileId,
        quotation.CanBeSent,
        quotation.HasChangesSinceSent,
        quotation.CanBeConvertedToSale,
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

    /// <summary>
    /// La fila del listado. <paramref name="hasItems"/> y <paramref name="sale"/> llegan
    /// resueltos por el handler y no se leen del agregado: la busqueda del listado no trae las
    /// lineas (la tabla no las pinta) y la venta vive en otra tabla, asi que <c>quotation.Items</c>
    /// aca esta vacia aunque la cotizacion tenga lineas.
    /// </summary>
    public static QuotationListItemDto ToListItemDto(
        this Quotation quotation,
        string? clientName,
        string? advisorEmail,
        bool hasItems,
        Sale? sale) => new(
        quotation.Id.Value,
        quotation.QuotationNumber,
        quotation.ClientId,
        clientName,
        quotation.AdvisorId.Value,
        advisorEmail,
        quotation.Status.ToString(),
        quotation.CreatedAt,
        quotation.Currency.ToCode(),
        quotation.Total,
        quotation.CanBeSent,
        hasItems && quotation.ValidUntil is not null && quotation.BillingAccount is not null,
        sale?.Id.Value,
        sale?.Status.ToString());

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
                request.BillingUsesBusinessName,
                request.IsStorePickup,
                request.BillsToFinalConsumer);

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
