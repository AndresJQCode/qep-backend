using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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

    /// <summary>La vista previa de la conversión (spec alerta UVT §5.3). Su desglose pasa por el mismo contexto de UVT que
    /// el del pedido, con la fecha con que se armó, para que diga lo mismo que dirá el pedido ya creado.</summary>
    Task<QuotationConversionPreviewResponse> ComposeConversionPreviewAsync(
        Guid tenantId, QuotationConversionPreviewDto preview, CancellationToken cancellationToken);
}

public sealed class OrderResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationWithholdingLookup withholdingLookup,
    IQuotationGeographyLookup geographyLookup,
    IQuotationBuyerAccountLookup buyerAccountLookup,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory)
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
            .Concat(order.Wholesale?.ApprovedBy is { } approvedBy ? [approvedBy] : [])
            .Distinct()
            .ToArray();
        var members = await advisorLookup.FindAsync(tenantId, memberIds, cancellationToken);
        var advisor = members.GetValueOrDefault(order.AdvisorId);
        // Sin empresa sólo llega el cálculo previo, que precia antes de que se elija la empresa que factura. Con empresa,
        // con o sin cuenta (spec 2026-11-17, R3), se busca su nombre.
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
                .Select(item => QuotationResponseComposer.ToItemResponse(item, products, order.GlobalScaleFloor) with
                {
                    // Mayorista fase 3: false = la agregó el mayorista; fuera de un pedido mayorista no aplica.
                    FromPurchaseOrder = order.Wholesale is null ? null : item.SourcePurchaseOrderLineId is not null,
                    IsGift = item.IsGift,
                })
                .ToArray(),
            // Sin el instante no se arma (spec 2026-11-16 F-8): CreatedAt no es opcional en el contrato, y un número sin
            // cotización detrás no tiene cuándo mostrar.
            order.QuotationId is { } quotationId
            && detail.QuotationNumber is { } number
            && detail.QuotationCreatedAt is { } quotationCreatedAt
                ? new SourceQuotationResponse(quotationId, number, quotationCreatedAt)
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
            buyerAccount is null ? null : new BuyerAccountResponse(buyerAccount.Id, buyerAccount.Email),
            await WholesaleAsync(order.Wholesale, members, cancellationToken));
    }

    // D-7: el nombre del distribuidor es el de su tenant, el mismo que Distribuidores ya le muestra al mayorista.
    private async Task<OrderWholesaleDto?> WholesaleAsync(
        OrderWholesaleState? wholesale,
        IReadOnlyDictionary<Guid, QuotationAdvisor> members,
        CancellationToken cancellationToken)
    {
        if (wholesale is null)
        {
            return null;
        }

        var buyerName = await tenantDirectory.GetDisplayNameAsync(new TenantId(wholesale.BuyerTenantId), cancellationToken);
        return new OrderWholesaleDto(
            new OrderTenantRefDto(wholesale.BuyerTenantId, buyerName),
            wholesale.PurchaseOrderId,
            wholesale.PurchaseOrderNumber,
            wholesale.Approval,
            wholesale.ApprovedAt,
            wholesale.ApprovedBy is { } approvedBy
                ? new OrderMemberRefDto(approvedBy, members.GetValueOrDefault(approvedBy)?.Label)
                : null,
            wholesale.CancelledByBuyer);
    }

    public async Task<OrderResponse> ComposeOrderAsync(Guid tenantId, OrderDto order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var taxUnits = await QuotationResponseComposer.TaxUnitContextAsync(
            tenantClock, withholdingLookup, tenantId, order.CreatedAt, order.WithholdingBreakdown, cancellationToken);
        var regionsById = await QuotationResponseComposer.PartyRegionsAsync(
            geographyLookup, order.Parties, cancellationToken);
        return OrderResponses.ToResponse(order, taxUnits, regionsById);
    }

    public async Task<QuotationConversionPreviewResponse> ComposeConversionPreviewAsync(
        Guid tenantId, QuotationConversionPreviewDto preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var taxUnits = await QuotationResponseComposer.TaxUnitContextAsync(
            tenantClock, withholdingLookup, tenantId, preview.CreatedAt, preview.WithholdingBreakdown, cancellationToken);
        return new QuotationConversionPreviewResponse(
            preview.RetentionAmount,
            preview.NetTotal,
            QuotationResponseComposer.ToBreakdownResponse(preview.WithholdingBreakdown, taxUnits),
            preview.TaxUnitChange);
    }
}

/// <summary>El <see cref="OrderResponse"/> de un <see cref="OrderDto"/>: lo usan los endpoints y el
/// compositor del detalle.</summary>
public static class OrderResponses
{
    private static readonly IReadOnlyDictionary<Guid, QuotationRegionRef> EmptyRegions =
        new Dictionary<Guid, QuotationRegionRef>();

    /// <param name="context">El año local del pedido y el último año de UVT cargado por país, para decir si la UVT del
    /// desglose está desactualizada y cuál tomaría (spec alerta UVT §5.4). Lo resuelve
    /// <see cref="IOrderResponseComposer.ComposeOrderAsync"/> con el calendario del tenant y Catalog.</param>
    internal static OrderResponse ToResponse(
        OrderDto order, TaxUnitContext context, IReadOnlyDictionary<Guid, QuotationRegionRef>? regionsById = null)
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
            // Una empresa sin cuenta (spec 2026-11-17, R3) se responde con la empresa y banco y número nulos.
            order.BillingAccount,
            order.Parties
                .Select(party => new QuotationPartyResponse(
                    party.Id, party.Role, party.Name, party.IdentificationNumber, party.Phone, party.Email,
                    party.Address, party.DepartmentId, party.CityId, party.PersonType, party.IdentificationType,
                    party.Country, party.CityName,
                    QuotationResponseComposer.ToRegionDto(party.RegionId, regionsById ?? EmptyRegions),
                    party.PostalCode))
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
            QuotationResponseComposer.ToBreakdownResponse(order.WithholdingBreakdown, context),
            order.OrderDate,
            order.RequiresShipping,
            order.InvoiceProvider,
            order.InvoiceNumber,
            DeliveryEditable: order.DeliveryEditable,
            Kind: order.Kind,
            Project: order.Project);
    }
}
