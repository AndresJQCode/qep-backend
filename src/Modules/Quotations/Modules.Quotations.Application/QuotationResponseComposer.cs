namespace Modules.Quotations.Application;

/// <summary>
/// Arma la respuesta HTTP de una cotización con **todo lo que su pantalla muestra**: el cliente
/// con su libreta de direcciones, el correo de la asesora y, por cada línea, el nombre, la
/// portada y las escalas de su producto.
///
/// Existe porque el detalle costaba cuatro consultas del navegador para dibujar una pantalla:
/// la cotización, la ficha del cliente, el padrón de miembros entero para un correo y hasta
/// doscientos productos para poner nombre a sus líneas. Nada de eso es un recurso que la
/// persona esté mirando por separado — es esta cotización, contada completa.
///
/// Vive en el borde HTTP y no en el <c>QuotationDto</c> porque es una decisión de presentación:
/// los handlers siguen devolviendo el agregado y sus totales, sin cargar con lo que una pantalla
/// necesita para pintarse. Se aplica a **todas** las respuestas de cotización, no sólo al GET:
/// el frontend guarda en su caché lo que devuelve cada mutación, así que una respuesta sin estos
/// datos dejaría la pantalla a medias después de agregar una línea.
/// </summary>
public interface IQuotationResponseComposer
{
    Task<QuotationResponse> ComposeAsync(
        Guid tenantId, QuotationDto quotation, CancellationToken cancellationToken);
}

public sealed class QuotationResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup)
    : IQuotationResponseComposer
{
    public async Task<QuotationResponse> ComposeAsync(
        Guid tenantId, QuotationDto quotation, CancellationToken cancellationToken)
    {
        var customer = await customerLookup.FindAsync(
            tenantId, quotation.ClientId, cancellationToken);

        var advisorEmails = await advisorLookup.FindEmailsAsync(
            tenantId, [quotation.AdvisorId], cancellationToken);

        // Sólo cuando la cotización eligió cuenta: una consulta más por pantalla, y ninguna en
        // el caso normal de un borrador recién creado.
        var company = quotation.BillingAccount is { } billing
            ? await companyLookup.FindAsync(tenantId, billing.CompanyId, cancellationToken)
            : null;

        var products = await productLookup.FindManyAsync(
            tenantId,
            quotation.Items.Select(item => item.ProductId).Distinct().ToArray(),
            cancellationToken);

        return new QuotationResponse(
            quotation.Id,
            quotation.QuotationNumber,
            quotation.ClientId,
            ToClientResponse(customer),
            quotation.AdvisorId,
            advisorEmails.GetValueOrDefault(quotation.AdvisorId),
            quotation.Status,
            quotation.CreatedAt,
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Currency,
            quotation.Subtotal,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.DiscountAmount,
            quotation.Total,
            quotation.CustomerVatSurplus,
            quotation.RetentionAmount,
            quotation.NetTotal,
            quotation.Notes,
            quotation.Parties.Select(ToPartyResponse).ToArray(),
            quotation.BillingUsesBusinessName,
            ToBillingResponse(quotation.BillingAccount, company),
            quotation.CreatedBy,
            quotation.UpdatedBy,
            quotation.UpdatedAt,
            quotation.SentAt,
            quotation.PdfFileId,
            quotation.Items.Select(item => ToItemResponse(item, products)).ToArray());
    }

    // Un cliente que no resuelve deja la cotización sin bloque de cliente en vez de tirar la
    // respuesta abajo: `ClientId` es una referencia blanda entre módulos, y una cotización
    // histórica tiene que poder leerse aunque su cliente se haya borrado.
    private static QuotationClientResponse? ToClientResponse(QuotationCustomerRef? customer) =>
        customer is null
            ? null
            : new QuotationClientResponse(
                customer.Id,
                customer.Cuc,
                customer.Name,
                customer.Phone,
                customer.Email,
                customer.Address,
                customer.CityId,
                customer.CityName,
                customer.DepartmentId,
                customer.DepartmentName,
                customer.BusinessName,
                customer.WithRetention,
                customer.VatSurplus,
                customer.IsActive,
                customer.UpdatedAt ?? default,
                (customer.Addresses ?? [])
                    .Select(address => new QuotationClientAddressResponse(
                        address.Id,
                        address.Name,
                        address.Address,
                        address.Phone,
                        address.CityId,
                        address.CityName,
                        address.DepartmentId,
                        address.DepartmentName,
                        address.IsPrincipal))
                    .ToArray());

    // El producto puede no existir más (se dio de baja y se borró): la línea conserva lo que la
    // cotización congeló —cantidad, precio, descuento— y el nombre queda vacío, que es lo único
    // que este lado no puede reconstruir.
    private static QuotationItemResponse ToItemResponse(
        QuotationItemDto item,
        IReadOnlyDictionary<Guid, QuotationProductRef> products)
    {
        products.TryGetValue(item.ProductId, out var product);

        return new QuotationItemResponse(
            item.Id,
            item.ProductId,
            product?.Name ?? string.Empty,
            product?.Code ?? string.Empty,
            product?.ImageUrl,
            (product?.Scales ?? [])
                .Select(scale => new QuotationItemPriceScaleResponse(
                    scale.FromUnit, scale.ToUnit, scale.Discount))
                .ToArray(),
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            item.DiscountAmount,
            item.Subtotal,
            item.TaxPercentage,
            item.TaxAmount,
            item.Position);
    }

    // La empresa que no resuelve deja el bloque con la cuenta guardada y sin razón social, por
    // el mismo motivo que el cliente: la cotización es histórica y tiene que poder leerse aunque
    // la empresa se haya borrado. Lo que se muestra es lo que se congeló, no lo que hay hoy.
    private static QuotationBillingResponse? ToBillingResponse(
        QuotationBillingAccountDto? account, QuotationCompanyRef? company) =>
        account is null
            ? null
            : new QuotationBillingResponse(
                account.CompanyId,
                company?.Name,
                company?.TaxId,
                account.BankName,
                account.AccountNumber,
                account.Currency);

    private static QuotationPartyResponse ToPartyResponse(QuotationPartyDto party) => new(
        party.Id,
        party.Role,
        party.Name,
        party.Phone,
        party.Email,
        party.Address,
        party.DepartmentId,
        party.CityId);
}
