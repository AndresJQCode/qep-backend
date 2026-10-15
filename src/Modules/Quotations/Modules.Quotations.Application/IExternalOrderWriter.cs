using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea que ya viene preciada por la plataforma de origen: precio unitario con IVA,
/// descuento y tasa. QEP no la vuelve a preciar contra el catálogo.</summary>
public sealed record ExternalOrderLine(
    Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

public sealed record ExternalOrderDraft(
    Guid TenantId,
    OrderSource Source,
    string ExternalReference,
    Guid ClientId,
    Guid AdvisorMembershipId,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest BillingAccount,
    IReadOnlyList<ExternalOrderLine> Lines,
    OrderPaymentStatus PaymentStatus,
    DateTimeOffset OccurredAt);

public sealed record ExternalOrderResult(Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal, string Currency);

/// <summary>
/// Spec 2026-10-15 §5.4 y spec de Shopify §6.2: el único camino para crear un pedido sin sesión HTTP.
/// Un módulo consumidor declara su propio puerto y su adaptador en Bootstrapper llama a éste; nunca
/// referencia <c>Modules.Quotations.Application</c>. A diferencia del pedido manual, las líneas llegan
/// preciadas y el cliente puede estar incompleto (spec de Shopify: «Solo deja pasar el pedido incompleto
/// si no viene de cotización»).
/// </summary>
public interface IExternalOrderWriter
{
    Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken cancellationToken);

    /// <summary>El estado de pago que avisa la plataforma. Sólo cambia si el pedido no tiene
    /// comprobantes propios; con ellos, lanza <c>order.order.payment_has_proofs</c>.</summary>
    Task SyncPaymentStatusAsync(Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken cancellationToken);

    Task CancelAsync(
        Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken);
}

public sealed class ExternalOrderService(
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductLookup productLookup,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    ITenantClock tenantClock)
    : IExternalOrderWriter
{
    public async Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var customer = await customerLookup.FindAsync(draft.TenantId, draft.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, draft.TenantId, draft.ClientId, allowIncomplete: true);
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, draft.TenantId, draft.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing account.");

        // La foto del producto se toma del catálogo; el precio no: es el que cobró la plataforma.
        var products = await productLookup.FindManyAsync(
            draft.TenantId, draft.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);
        var lines = draft.Lines
            .Select(line =>
            {
                products.TryGetValue(line.ProductId, out var product);
                return new OrderLineInput(
                    line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage,
                    product is null ? null : new QuotationProductSnapshot(product.Code, product.Name));
            })
            .ToArray();
        var advisor = new MemberId(draft.AdvisorMembershipId);
        var header = new OrderHeader(
            draft.ClientId, advisor, billingAccount.Currency, draft.PaymentMethod, IsRetail: false,
            GlobalScaleFloor: null, customer.WithRetention, customer.VatSurplus);

        // Mismo consecutivo que los demás pedidos; el contador y el pedido se confirman juntos.
        var calendar = await tenantClock.GetAsync(draft.TenantId, cancellationToken);
        var year = calendar.Today.Year;
        var format = await numberingFormats.GetAsync(draft.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var sequence = await numberGenerator.NextAsync(draft.TenantId, counterYear, cancellationToken);
        var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);
        var order = Order.CreateExternal(
            OrderId.New(), draft.TenantId, orderNumber, header, draft.Parties.ToDomain(), billingAccount, lines,
            draft.PaymentStatus, draft.Notes, advisor, [], draft.OccurredAt, draft.Source, draft.ExternalReference);

        orderRepository.Add(order);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, memberId: null,
            OrderChangeSummary.CreatedFromExternal(draft.Source, draft.ExternalReference), draft.OccurredAt));
        auditPublisher.PublishByIntegration(
            draft.TenantId, "quotation.order.created", order.Id.ToString(), "success", draft.OccurredAt);
        lifecycleEvents.PublishOrderCreated(order, draft.OccurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ExternalOrderResult(order.Id.Value, order.OrderNumber, order.Total, order.NetTotal, order.Currency);
    }

    public async Task SyncPaymentStatusAsync(
        Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        if (order.PaymentStatus == status)
        {
            return;
        }

        order.SyncExternalPaymentStatus(status, now);
        auditPublisher.PublishByIntegration(
            tenantId, "quotation.order.payment_status_synced", order.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(
        Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        var by = new MemberId(cancelledByMembershipId);
        order.Cancel(by, reason, now);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, by,
            OrderChangeSummary.Cancelled(order.CancellationReason!), now));
        auditPublisher.PublishByIntegration(tenantId, "quotation.order.cancelled", order.Id.ToString(), "success", now);
        lifecycleEvents.PublishOrderCancelled(order, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
