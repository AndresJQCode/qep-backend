using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-11-18 §5.5: el pedido visto por un facturador externo. <see cref="GetForInvoicingAsync"/> repite las precondiciones de
/// <see cref="InvoiceOrderHandler"/> —y lanza los mismos 422— antes de armar la foto; <see cref="MarkInvoicedAsync"/> marca con
/// <see cref="Order.InvoiceExternally"/>, que no vuelve a exigir el pago: se validó una vez, al armar, y la factura ya existe afuera.
/// <see cref="RevertExternalInvoicingAsync"/> quita esa marca cuando la factura se anuló afuera.
/// </summary>
public sealed class ExternalOrderInvoicingService(
    IOrderRepository orders,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IQuotationSettingsStore settingsStore,
    ITenantDirectory tenantDirectory,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationGeographyLookup geographyLookup)
    : IExternalOrderInvoicing
{
    public async Task<ExternalInvoicingOrder?> GetForInvoicingAsync(
        Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        if (order is null)
        {
            return null;
        }

        var settings = await settingsStore.GetOrderSettingsAsync(tenantId, cancellationToken);
        order.EnsureCanInvoice(settings.RequireFullPaymentToInvoice);
        await BilledPersonTypeRequired.EnsureSatisfiedAsync(tenantDirectory, customerLookup, tenantId, order, cancellationToken);
        await BilledCustomerFieldsRequired.EnsureSatisfiedAsync(tenantDirectory, customerLookup, tenantId, order, cancellationToken);

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var party = await ResolvePartyAsync(tenantId, order, cancellationToken);
        var advisors = await advisorLookup.FindAsync(tenantId, [order.AdvisorId.Value], cancellationToken);
        var items = order.Items.OrderBy(item => item.Position).ToList();
        var shippingAddress = FirstNonBlank(order.Shipping?.Address, null);
        var shippingCity = await ShippingCityAsync(order.Shipping, cancellationToken);
        // «Los mismos del cliente» no guarda parte de envío: el despacho va a la ficha del cliente, la misma condición de BilledCustomerFieldsRequired.
        if (order.Shipping is null && !order.IsStorePickup && order.ShippingDetails != ShippingDetails.None)
        {
            var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
            shippingAddress = FirstNonBlank(customer?.Address, null);
            shippingCity = await CustomerCityAsync(customer, cancellationToken);
        }

        return new ExternalInvoicingOrder(
            order.Id.Value,
            order.OrderNumber,
            order.Currency,
            calendar.Today,
            order.CashOnDelivery,
            [.. items.Select(item => new ExternalInvoicingLine(
                item.VariantId,
                item.ProductName ?? item.ProductCode ?? string.Empty,
                item.Quantity,
                item.Subtotal,
                item.DiscountPercentage,
                item.TaxPercentage,
                item.TaxAmount,
                item.IsGift,
                item.UnitPrice))],
            order.FreightSubtotal,
            order.FreightTaxAmount,
            order.FreightTaxPercentage,
            items.Sum(item => item.Subtotal + item.TaxAmount),
            order.Total,
            order.RetentionAmount,
            order.NetTotal,
            [.. order.AppliedWithholdings.Select(withholding => new ExternalInvoicingWithholding(
                withholding.TaxRateId, withholding.Name, withholding.Type, withholding.Amount, withholding.Percentage))],
            [.. order.PaymentProofs.OrderBy(proof => proof.UploadedAt).ThenBy(proof => proof.Id.Value)
                .Select(proof => new ExternalInvoicingProof(proof.Id.Value, proof.Amount))],
            order.BillingAccount?.CompanyId,
            order.BillingAccount?.BankName,
            order.BillingAccount?.AccountNumber,
            order.BuyerNotes,
            party,
            order.Version,
            order.AdvisorId.Value,
            advisors.GetValueOrDefault(order.AdvisorId.Value)?.Label,
            shippingAddress,
            shippingCity);
    }

    // La ciudad de la ficha del cliente: sin país o CO, el nombre que da Geography; fuera de Colombia, el texto de la ficha.
    private async Task<string?> CustomerCityAsync(QuotationCustomerRef? customer, CancellationToken cancellationToken)
    {
        if (customer is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(customer.Country) && customer.Country != "CO")
        {
            return FirstNonBlank(customer.CityName, null);
        }

        if (customer.CityId is not { } cityId)
        {
            return null;
        }

        var names = await geographyLookup.FindCityNamesAsync([cityId], cancellationToken);
        return names.GetValueOrDefault(cityId);
    }

    // La ciudad de la parte de envío: el nombre que da Geography en Colombia, el texto escrito a mano fuera de ella (el mismo criterio que el comprador).
    private async Task<string?> ShippingCityAsync(OrderParty? shipping, CancellationToken cancellationToken)
    {
        if (shipping is null)
        {
            return null;
        }

        if (shipping.Country is not null)
        {
            return FirstNonBlank(shipping.CityName, null);
        }

        if (shipping.CityId is not { } cityId)
        {
            return null;
        }

        var names = await geographyLookup.FindCityNamesAsync([cityId], cancellationToken);
        return names.GetValueOrDefault(cityId);
    }

    public async Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        (await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken))?.Version;

    public async Task<bool> IsMarkedAsync(
        Guid tenantId, Guid orderId, string provider, string number, CancellationToken cancellationToken) =>
        await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken) is { IsInvoiced: true } order
        && string.Equals(order.InvoiceProvider, provider, StringComparison.Ordinal)
        && string.Equals(order.InvoiceNumber, number, StringComparison.Ordinal);

    public async Task<IReadOnlyDictionary<Guid, ExternalInvoicingOrderLabel>> ListLabelsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var rows = await orders.FindNumbersAndClientsAsync(
            tenantId, [.. orderIds.Distinct().Select(id => new OrderId(id))], cancellationToken);
        if (rows.Count == 0)
        {
            return new Dictionary<Guid, ExternalInvoicingOrderLabel>();
        }

        // El nombre, como en la lista de pedidos (OrderListing): una sola consulta para todos los clientes.
        var names = await customerLookup.FindNamesAsync(
            tenantId, [.. rows.Values.Select(row => row.ClientId).Distinct()], cancellationToken);
        return rows.ToDictionary(
            row => row.Key.Value,
            row => new ExternalInvoicingOrderLabel(row.Value.OrderNumber, names.GetValueOrDefault(row.Value.ClientId)));
    }

    public async Task MarkInvoicedAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        string externalId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        try
        {
            await MarkAndSaveAsync(tenantId, orderId, provider, number, externalId, memberId, cancellationToken);
            return;
        }
        catch (RequestConcurrencyException)
        {
            // La factura ya existe afuera: otra escritura del pedido que ganó no puede impedir la marca. Se relee y se decide de nuevo.
            unitOfWork.DiscardPendingChanges();
        }

        try
        {
            await MarkAndSaveAsync(tenantId, orderId, provider, number, externalId, memberId, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            unitOfWork.DiscardPendingChanges();
            throw;
        }
    }

    private async Task MarkAndSaveAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        string externalId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var order = await orders.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        var versionBefore = order.Version;
        var now = clock.UtcNow;
        var member = new MemberId(memberId);

        order.InvoiceExternally(provider, number, externalId, member, now);
        if (order.Version == versionBefore)
        {
            // Ya estaba marcada con ese proveedor y número: ni historial ni auditoría repetidos.
            return;
        }

        orders.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Invoiced, member, OrderChangeSummary.InvoicedExternally(provider, number), now));
        auditPublisher.PublishWithDetails(
            tenantId,
            executionContext.SubjectId,
            "quotation.order.invoiced",
            order.Id.ToString(),
            "success",
            now,
            new Dictionary<string, string?> { ["provider"] = provider });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RevertExternalInvoicingAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RevertAndSaveAsync(tenantId, orderId, provider, number, memberId, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            // La factura ya está anulada afuera: otra escritura del pedido que ganó no puede dejarlo marcado. Se relee y se decide de nuevo.
            unitOfWork.DiscardPendingChanges();
        }

        try
        {
            return await RevertAndSaveAsync(tenantId, orderId, provider, number, memberId, cancellationToken);
        }
        catch (RequestConcurrencyException)
        {
            unitOfWork.DiscardPendingChanges();
            throw;
        }
    }

    private async Task<bool> RevertAndSaveAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var order = await orders.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        var now = clock.UtcNow;
        var member = new MemberId(memberId);

        if (!order.RevertExternalInvoicing(provider, number, member, now))
        {
            return false;
        }

        orders.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.InvoicingReverted, member,
            OrderChangeSummary.ExternalInvoiceAnnulled(provider, number), now));
        auditPublisher.PublishWithDetails(
            tenantId,
            executionContext.SubjectId,
            "quotation.order.invoice_reverted",
            order.Id.ToString(),
            "success",
            now,
            new Dictionary<string, string?> { ["provider"] = provider, ["number"] = number });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Spec §6.2: los datos salen de la parte facturada del pedido y, lo que falte, de la ficha del cliente.
    private async Task<ExternalInvoicingParty> ResolvePartyAsync(Guid tenantId, Order order, CancellationToken cancellationToken)
    {
        var source = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing);
        if (source == BilledPartySource.FinalConsumer)
        {
            return new ExternalInvoicingParty(true, null, null, string.Empty, string.Empty, null, null, null, null, null, null);
        }

        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        var customerPersonType = PartyFiscalTypesParser.ParsePersonType(customer?.PersonType);
        var customerIdentificationType = PartyFiscalTypesParser.ParseIdentificationType(customer?.IdentificationType);
        var billing = source == BilledPartySource.BillingParty ? order.Billing : null;

        var personType = billing?.PersonType ?? customerPersonType;
        var name = billing?.Name is { Length: > 0 } billingName
            ? billingName
            : CustomerName(order, customer, personType);
        // Ciudad, departamento, país y nombre de ciudad salen juntos de una sola fuente: la parte de facturación si declara ciudad o
        // país, y si no, la ficha. La facturación colombiana guarda CO (una vieja, null con CityId: el país efectivo la lee como CO).
        // Con el país pendiente (spec 2026-11-27) no se sabe a qué país factura: no se completa nada con la ficha y el armador de
        // Siigo pide el país (customer_country_missing).
        var countryPending = billing?.IsCountryPending == true;
        var billingDeclaresPlace = billing is not null
            && (billing.CityId is not null
                || !string.IsNullOrWhiteSpace(billing.CityName)
                || !string.IsNullOrWhiteSpace(billing.EffectiveCountry));
        var (cityId, departmentId, country, cityName) = countryPending
            ? ((Guid?)null, (Guid?)null, (string?)null, (string?)null)
            : billingDeclaresPlace
                ? (billing!.CityId, billing.DepartmentId, FirstNonBlank(billing.EffectiveCountry, null), FirstNonBlank(billing.CityName, null))
                : (customer?.CityId, customer?.DepartmentId, FirstNonBlank(customer?.Country, null), FirstNonBlank(customer?.CityName, null));
        return new ExternalInvoicingParty(
            false,
            personType,
            billing?.IdentificationType ?? customerIdentificationType,
            FirstNonBlank(billing?.IdentificationNumber, customer?.IdentificationNumber) ?? string.Empty,
            name,
            FirstNonBlank(billing?.Address, customer?.Address),
            cityId,
            departmentId,
            country,
            FirstNonBlank(billing?.Email, customer?.Email),
            FirstNonBlank(billing?.Phone, customer?.Phone),
            cityName,
            countryPending);
    }

    // La razón social cuando el pedido factura a ella (BillingUsesBusinessName) o la persona es jurídica; si no, el nombre de contacto.
    private static string CustomerName(Order order, QuotationCustomerRef? customer, PartyPersonType? personType)
    {
        if (customer is null)
        {
            return string.Empty;
        }

        var useBusinessName = order.BillingUsesBusinessName || personType == PartyPersonType.Juridica;
        return useBusinessName && !string.IsNullOrWhiteSpace(customer.BusinessName) ? customer.BusinessName : customer.Name;
    }

    private static string? FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : string.IsNullOrWhiteSpace(second) ? null : second.Trim();
}
