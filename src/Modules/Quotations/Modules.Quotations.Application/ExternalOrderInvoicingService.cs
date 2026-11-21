using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-11-18 §5.5: el pedido visto por un facturador externo. <see cref="GetForInvoicingAsync"/> repite las precondiciones de
/// <see cref="InvoiceOrderHandler"/> —y lanza los mismos 422— antes de armar la foto; <see cref="MarkInvoicedAsync"/> marca con
/// <see cref="Order.InvoiceExternally"/>, que no vuelve a exigir el pago: se validó una vez, al armar, y la factura ya existe afuera.
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
    IQuotationAdvisorLookup advisorLookup)
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
                item.TaxAmount))],
            order.FreightSubtotal,
            order.FreightTaxAmount,
            order.FreightTaxPercentage,
            items.Sum(item => item.Subtotal + item.TaxAmount),
            order.Total,
            order.RetentionAmount,
            order.NetTotal,
            [.. order.AppliedWithholdings.Select(withholding => new ExternalInvoicingWithholding(
                withholding.TaxRateId, withholding.Name, withholding.Type, withholding.Amount))],
            [.. order.PaymentProofs.OrderBy(proof => proof.UploadedAt).ThenBy(proof => proof.Id.Value)
                .Select(proof => new ExternalInvoicingProof(proof.Id.Value, proof.Amount))],
            order.BillingAccount?.CompanyId,
            order.BillingAccount?.BankName,
            order.BillingAccount?.AccountNumber,
            order.BuyerNotes,
            party,
            order.Version,
            order.AdvisorId.Value,
            advisors.GetValueOrDefault(order.AdvisorId.Value)?.Label);
    }

    public async Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        (await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken))?.Version;

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
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Invoiced, member, OrderChangeSummary.Invoiced(), now));
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
        var billingCityComplete = billing?.CityId is not null;
        return new ExternalInvoicingParty(
            false,
            personType,
            billing?.IdentificationType ?? customerIdentificationType,
            FirstNonBlank(billing?.IdentificationNumber, customer?.IdentificationNumber) ?? string.Empty,
            name,
            FirstNonBlank(billing?.Address, customer?.Address),
            billingCityComplete ? billing!.CityId : customer?.CityId,
            billingCityComplete ? billing!.DepartmentId : customer?.DepartmentId,
            FirstNonBlank(billing?.Country, customer?.Country),
            FirstNonBlank(billing?.Email, customer?.Email),
            FirstNonBlank(billing?.Phone, customer?.Phone));
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
