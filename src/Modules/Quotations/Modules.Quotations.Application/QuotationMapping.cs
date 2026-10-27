using BuildingBlocks.Domain.Pricing;
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
        quotation.Currency,
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
        quotation.PartyWithRetention,
        quotation.PartyVatSurplus,
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
        quotation.CanBeConvertedToOrder,
        quotation.Items.Select(item => ToDto(item, quotation.PriceRounding)).ToArray(),
        quotation.Version,
        quotation.ShippingDtoOf(),
        quotation.FreightSubtotal,
        quotation.FreightTaxAmount,
        quotation.GlobalScaleFloor,
        quotation.IsRetail);

    /// <summary>La entrega por envío con forma fija, también en recogida (spec 2026-10-17 §7).</summary>
    public static ShippingDto ShippingDtoOf(this Quotation quotation) => new(
        quotation.IsStorePickup,
        quotation.CarrierId is { } carrierId
            ? new ShippingCarrierDto(carrierId, quotation.CarrierName ?? string.Empty)
            : null,
        quotation.FreightChargeMode?.ToString(),
        quotation.FreightAmount,
        quotation.FreightTaxPercentage,
        quotation.FreightSubtotal,
        quotation.FreightTaxAmount,
        quotation.CashOnDelivery);

    private static QuotationBillingAccountDto? ToDto(QuotationBillingAccount? account) =>
        account is null
            ? null
            : new QuotationBillingAccountDto(
                account.CompanyId, account.BankName, account.AccountNumber, account.Currency);

    private static QuotationPartyDto ToDto(QuotationParty party) => new(
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

    /// <summary>
    /// La fila del listado. <paramref name="hasItems"/> y <paramref name="order"/> llegan
    /// resueltos por el handler y no se leen del agregado: la busqueda del listado no trae las
    /// lineas (la tabla no las pinta) y el pedido vive en otra tabla, asi que <c>quotation.Items</c>
    /// aca esta vacia aunque la cotizacion tenga lineas.
    /// </summary>
    public static QuotationListItemDto ToListItemDto(
        this Quotation quotation,
        string? clientName,
        string? advisorName,
        bool hasItems,
        Order? order) => new(
        quotation.Id.Value,
        quotation.QuotationNumber,
        quotation.ClientId,
        clientName,
        quotation.AdvisorId.Value,
        advisorName,
        quotation.Status.ToString(),
        quotation.CreatedAt,
        quotation.Currency,
        quotation.Total,
        quotation.CanBeSent,
        hasItems && quotation.ValidUntil is not null && quotation.BillingAccount is not null,
        order?.Id.Value,
        order?.Status.ToString(),
        quotation.ShippingDtoOf());

    private static QuotationItemDto ToDto(QuotationItem item, PriceRounding rounding) => new(
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
        item.ProductName);

    /// <summary>El modo tal como viaja (nombre exacto del enum) al enum del dominio. El validador ya rechaza un
    /// nombre que no existe con el campo en <c>errors</c>; esto es el respaldo para quien llegue sin pasar por
    /// él: un 422 con código y no un 500. Ni números ni otra capitalización: el contrato son los tres nombres.</summary>
    public static FreightChargeMode? ToFreightChargeMode(string? mode, string errorPrefix)
    {
        if (mode is null)
        {
            return null;
        }

        if (Enum.GetNames<FreightChargeMode>().Contains(mode, StringComparer.Ordinal)
            && Enum.TryParse<FreightChargeMode>(mode, ignoreCase: false, out var parsed))
        {
            return parsed;
        }

        throw new QuotationsDomainException(
            $"{errorPrefix}.freight_charge_mode_invalid",
            $"'{mode}' is not a valid freight charge mode.");
    }

    public static QuotationParties ToDomain(this QuotationPartiesRequest? request) =>
        request is null
            ? QuotationParties.Empty
            : new QuotationParties(
                request.Billing.ToDomain(),
                request.Shipping.ToDomain(),
                request.BillingUsesBusinessName,
                request.IsStorePickup,
                request.BillsToFinalConsumer,
                request.BillingWithRetention,
                request.BillingVatSurplus);

    internal static QuotationPartyDetails? ToDomain(this QuotationPartyRequest? request) =>
        request is null
            ? null
            : new QuotationPartyDetails
            {
                Name = request.Name,
                IdentificationNumber = request.IdentificationNumber,
                PersonType = PartyFiscalTypesParser.ParsePersonType(request.PersonType),
                IdentificationType = PartyFiscalTypesParser.ParseIdentificationType(request.IdentificationType),
                Phone = request.Phone,
                Email = request.Email,
                Address = request.Address,
                DepartmentId = request.DepartmentId,
                CityId = request.CityId
            };
}
