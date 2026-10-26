using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea que ya viene preciada por la plataforma de origen: precio unitario con IVA,
/// descuento y tasa. QEP no la vuelve a preciar contra el catálogo.
///
/// Por variante (spec 2026-10-20 §4): <paramref name="VariantId"/> es la clave de la línea y de su
/// foto; <paramref name="ProductId"/> es su producto, que la línea guarda para agrupar por producto.
/// Viajan los dos porque el consumidor ya los tiene (Shopify empareja por el <c>Code</c> de la
/// variante y su lookup devuelve ambos) y así el pedido no depende de que el catálogo siga
/// devolviendo la variante para saber de qué producto era.</summary>
public sealed record ExternalOrderLine(
    Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

/// <summary>
/// El pedido que trae una integración. <see cref="Shipping"/> es el envío que cobró la plataforma (flete §10): no
/// se valida contra el recaudo actual de la transportadora ni contra los modos de quién paga habilitados en el
/// tenant (D-F18), porque ya se cobró allá. <see cref="ExternalKey"/> hace idempotente la creación (D-S23): con
/// la misma clave, la segunda llamada devuelve el pedido que ya existe.
/// </summary>
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
    DateTimeOffset OccurredAt,
    ShippingDetails? Shipping = null,
    string? ExternalKey = null,
    string? ExternalShippingTitle = null);

/// <summary><see cref="AlreadyExisted"/> dice que la clave externa ya tenía pedido y no se creó otro: el
/// consumidor marca su espejo igual, sin volver a publicar nada. <see cref="ClientId"/> es el cliente del pedido
/// guardado, que con <see cref="AlreadyExisted"/> puede no ser el del borrador: el espejo anota ése.</summary>
public sealed record ExternalOrderResult(
    Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal, string Currency, Guid ClientId, bool AlreadyExisted = false,
    ExternalShippingParty? Shipping = null);

/// <summary>La parte de envío de un pedido externo (spec 2026-10-26 §4.4). Sin documento: a quien se le entrega no
/// se le factura. Los seis campos son los de <c>OrderParty</c>, tal como el consumidor los guarda en su huella.</summary>
public sealed record ExternalShippingParty(
    string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId);

/// <summary>Qué pasó con una parte de envío: <see cref="Applied"/> y <see cref="AlreadyApplied"/> son éxito (la segunda es
/// un reintento); el resto son las razones por las que el pedido ya no admite el cambio.</summary>
public enum ExternalShippingOutcomeKind
{
    Applied,
    AlreadyApplied,
    NotFound,
    NotPending,
    StorePickup,
    ShippingPresent,
    ShippingChanged,
    PartyIncomplete,
}

/// <summary><see cref="Party"/> es la parte como quedó guardada (normalizada): la huella de Shopify sale de acá, nunca del
/// borrador. <see cref="Status"/> viaja con <see cref="ExternalShippingOutcomeKind.NotPending"/>, para que el consumidor
/// distinga un pedido aprobado de uno cancelado. Con <see cref="ExternalShippingOutcomeKind.NotFound"/> viajan nulos.</summary>
public sealed record ExternalShippingOutcome(ExternalShippingOutcomeKind Kind, ExternalShippingParty? Party, OrderStatus? Status);

/// <summary>Los códigos propios del camino externo que cruzan capas.</summary>
public static class ExternalOrderErrors
{
    /// <summary>Lo lanza <c>QuotationsUnitOfWork</c> al chocar con <c>IX_orders_tenant_external_key</c>. No llega a
    /// HTTP: <see cref="ExternalOrderService"/> lo resuelve devolviendo el pedido que ya existe.</summary>
    public const string ExternalKeyTaken = "order.order.external_key_taken";
}

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

    /// <summary>El pedido que ya entró con esa clave (D-S23), como lo devolvería un alta repetida
    /// (<see cref="ExternalOrderResult.AlreadyExisted"/>), o <c>null</c>. Sólo lee y sólo ve el tenant y el origen
    /// pedidos. El consumidor lo usa cuando necesita saber si un pedido llegó sin intentar crearlo (Shopify §6.3:
    /// una fila en espera que se canceló).</summary>
    Task<ExternalOrderResult?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-26 §4.4: pone la parte de envío que el pedido no tenía y quita de las notas las líneas
    /// que empiezan por <paramref name="notesLinePrefix"/>. Las razones por las que no se puede viajan en el
    /// resultado; la concurrencia (<c>concurrency.conflict</c>) se propaga y el trabajo reintenta.</summary>
    Task<ExternalShippingOutcome> AttachShippingAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty shipping, string notesLinePrefix, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-26 §4.6: cambia sólo el departamento y la ciudad de la parte de envío, si sigue siendo
    /// <paramref name="expected"/> (la que guardó el consumidor). Mismos resultados y concurrencia que
    /// <see cref="AttachShippingAsync"/>.</summary>
    Task<ExternalShippingOutcome> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken);
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

        // D-S23: con clave, un pedido que ya entró se devuelve tal cual, sin escribir nada. Va antes que todo lo
        // demás: si el cliente quedó inactivo después, el reintento igual encuentra su pedido.
        var externalKey = string.IsNullOrWhiteSpace(draft.ExternalKey) ? null : draft.ExternalKey.Trim();
        if (externalKey is not null
            && await orderRepository.FindByExternalKeyAsync(
                draft.TenantId, draft.Source, externalKey, cancellationToken) is { } existing)
        {
            return Existing(existing);
        }

        var customer = await customerLookup.FindAsync(draft.TenantId, draft.ClientId, cancellationToken);
        OrderCustomerEligibility.Ensure(customer, draft.TenantId, draft.ClientId, allowIncomplete: true);
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, draft.TenantId, draft.BillingAccount, cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing account.");

        // La foto de la variante se toma del catálogo; el precio no: es el que cobró la plataforma.
        var products = await productLookup.FindManyAsync(
            draft.TenantId, draft.Lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken);
        var lines = draft.Lines
            .Select(line =>
            {
                products.TryGetValue(line.VariantId, out var product);
                return new OrderLineInput(
                    line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage,
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
        var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var sequence = await numberGenerator.NextAsync(draft.TenantId, counterYear, cancellationToken);
            var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);
            var order = Order.CreateExternal(
                OrderId.New(), draft.TenantId, orderNumber, header, draft.Parties.ToDomain(), billingAccount, lines,
                draft.PaymentStatus, draft.Notes, advisor, [], draft.OccurredAt, draft.Source, draft.ExternalReference,
                draft.Shipping ?? ShippingDetails.None, externalKey, draft.ExternalShippingTitle);

            orderRepository.Add(order);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, memberId: null,
                OrderChangeSummary.CreatedFromExternal(draft.Source, draft.ExternalReference), draft.OccurredAt));
            auditPublisher.PublishByIntegration(
                draft.TenantId, "quotation.order.created", order.Id.ToString(), "success", draft.OccurredAt);
            lifecycleEvents.PublishOrderCreated(order, draft.OccurredAt);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ExternalOrderResult(
                order.Id.Value, order.OrderNumber, order.Total, order.NetTotal, order.Currency, order.ClientId,
                Shipping: ToExternal(order.Shipping));
        }
        catch (QuotationsDomainException error)
            when (externalKey is not null && error.Code == ExternalOrderErrors.ExternalKeyTaken)
        {
            // Otra llamada con la misma clave guardó primero y el índice cerró la carrera. Se sigue abajo: la
            // transacción se deshace en el finally (el consecutivo vuelve atrás, sin huecos) antes de leer.
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        // Lo que quedó pendiente del intento fallido (el pedido, su historial, el evento y la auditoría) no debe
        // volver a intentarse en el próximo guardado de este scope.
        unitOfWork.DiscardPendingChanges();
        // Sólo se llega acá por el catch, que exige la clave.
        var winner = await orderRepository.FindByExternalKeyAsync(
                draft.TenantId, draft.Source, externalKey!, cancellationToken)
            ?? throw new InvalidOperationException(
                "The external key was taken, but no committed order carries it.");
        return Existing(winner);
    }

    public async Task<ExternalOrderResult?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(externalKey) ? null : externalKey.Trim();
        return key is not null
            && await orderRepository.FindByExternalKeyAsync(tenantId, source, key, cancellationToken) is { } order
                ? Existing(order)
                : null;
    }

    private static ExternalOrderResult Existing(Order order) =>
        new(order.Id.Value, order.OrderNumber, order.Total, order.NetTotal, order.Currency, order.ClientId, AlreadyExisted: true,
            Shipping: ToExternal(order.Shipping));

    private static ExternalShippingParty? ToExternal(OrderParty? party) =>
        party is null ? null : new(party.Name, party.Phone, party.Email, party.Address, party.DepartmentId, party.CityId);

    private static QuotationPartyDetails ToDetails(ExternalShippingParty party) => new()
    {
        Name = party.Name,
        Phone = party.Phone,
        Email = party.Email,
        Address = party.Address,
        DepartmentId = party.DepartmentId,
        CityId = party.CityId,
    };

    public Task<ExternalShippingOutcome> AttachShippingAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty shipping, string notesLinePrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        return ChangeShippingAsync(
            tenantId, orderId, (order, now) => order.AttachExternalShipping(ToDetails(shipping), notesLinePrefix, now), cancellationToken);
    }

    public Task<ExternalShippingOutcome> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ChangeShippingAsync(
            tenantId, orderId,
            (order, now) => order.CorrectExternalShippingCity(ToDetails(expected), departmentId, cityId, now),
            cancellationToken);
    }

    // Sin auditoría ni historial acá (desvío S9): la entrada la escribe el consumidor, que sabe el sinónimo y el operador.
    // Un código del dominio que no es del camino conocido (p. ej. source_invalid) se propaga: es un error de quien llama.
    private async Task<ExternalShippingOutcome> ChangeShippingAsync(
        Guid tenantId, Guid orderId, Func<Order, DateTimeOffset, bool> change, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        if (order is null)
        {
            return new ExternalShippingOutcome(ExternalShippingOutcomeKind.NotFound, null, null);
        }

        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        ExternalShippingOutcomeKind kind;
        try
        {
            kind = change(order, now) ? ExternalShippingOutcomeKind.Applied : ExternalShippingOutcomeKind.AlreadyApplied;
        }
        catch (QuotationsDomainException error) when (RefusalKind(error.Code) is { } refused)
        {
            return new ExternalShippingOutcome(
                refused, ToExternal(order.Shipping), refused == ExternalShippingOutcomeKind.NotPending ? order.Status : null);
        }

        if (kind == ExternalShippingOutcomeKind.Applied)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ExternalShippingOutcome(kind, ToExternal(order.Shipping), null);
    }

    private static ExternalShippingOutcomeKind? RefusalKind(string code) => code switch
    {
        "order.order.not_pending" => ExternalShippingOutcomeKind.NotPending,
        "order.order.store_pickup" => ExternalShippingOutcomeKind.StorePickup,
        "order.order.shipping_party_present" => ExternalShippingOutcomeKind.ShippingPresent,
        "order.order.shipping_party_changed" => ExternalShippingOutcomeKind.ShippingChanged,
        "order.shipping.party_incomplete" => ExternalShippingOutcomeKind.PartyIncomplete,
        _ => null,
    };

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
