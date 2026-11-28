using BuildingBlocks.Application;
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
    string? ExternalShippingTitle = null,
    string? BuyerNotes = null);

/// <summary><see cref="AlreadyExisted"/> dice que la clave externa ya tenía pedido y no se creó otro: el
/// consumidor marca su espejo igual, sin volver a publicar nada. <see cref="ClientId"/> es el cliente del pedido
/// guardado, que con <see cref="AlreadyExisted"/> puede no ser el del borrador: el espejo anota ése.</summary>
public sealed record ExternalOrderResult(
    Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal, string Currency, Guid ClientId, bool AlreadyExisted = false,
    ExternalShippingParty? Shipping = null);

/// <summary>La parte de envío de un pedido externo (spec 2026-10-26 §4.4). Sin documento: a quien se le entrega no
/// se le factura. Los campos son los de <c>OrderParty</c>, tal como el consumidor los guarda en su huella.
/// <paramref name="PostalCode"/> (spec 2026-11-24) sale con la parte guardada; la corrección del asesor lo ignora y conserva el
/// que tenga el pedido mientras no cambie el departamento. Fuera de Colombia (la entrega editable, spec 2026-11-24 §11) van <paramref name="Country"/>,
/// <paramref name="CityName"/> y, si el país tiene regiones, <paramref name="RegionId"/>, sin ids de DIVIPOLA;
/// <paramref name="Country"/> en <c>null</c> es Colombia, como en el dominio.</summary>
public sealed record ExternalShippingParty(
    string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId, string? PostalCode = null,
    string? Country = null, string? CityName = null, Guid? RegionId = null);

/// <summary>
/// Spec 2026-10-26 D-C12: lo que el recorrido de la historia previa necesita de un pedido para decidir si le falta la parte de
/// envío: su estado, si es de recoger en tienda, su parte de envío (<c>null</c> si no tiene) y sus notas tal cual están
/// guardadas (<c>null</c> si no tiene). Sólo lee. <see cref="IsEditable"/> es <c>Order.IsEditable</c> (spec 2026-11-10 §3.6:
/// abierto, sin preparar y sin facturar): el estado solo ya no dice si el pedido admite la parte de envío.
/// </summary>
public sealed record ExternalShippingState(
    OrderStatus Status, bool IsStorePickup, ExternalShippingParty? Shipping, string? Notes, bool IsEditable);

/// <summary>Qué pasó con una parte de envío: <see cref="Applied"/> y <see cref="AlreadyApplied"/> son éxito (la segunda es
/// un reintento); el resto son las razones por las que el pedido ya no admite el cambio.</summary>
public enum ExternalShippingOutcomeKind
{
    Applied,
    AlreadyApplied,
    NotFound,
    NotPending,

    /// <summary>El pedido no es de una plataforma (<c>order.order.source_invalid</c>): no toma su parte de envío de ella.
    /// Reintentar no lo arregla.</summary>
    NotExternal,
    StorePickup,
    ShippingPresent,
    ShippingChanged,
    PartyIncomplete,
}

/// <summary><see cref="Party"/> es la parte como quedó guardada (normalizada): la huella de Shopify sale de acá, nunca del
/// borrador. <see cref="Status"/> viaja con <see cref="ExternalShippingOutcomeKind.NotPending"/>, para que el consumidor
/// distinga un pedido abierto pero congelado de uno cancelado o archivado. Con <see cref="ExternalShippingOutcomeKind.NotFound"/> viajan nulos.</summary>
public sealed record ExternalShippingOutcome(ExternalShippingOutcomeKind Kind, ExternalShippingParty? Party, OrderStatus? Status);

/// <summary>Una línea del pedido como la compara la sincronización de ediciones (spec 2026-11-14): lo guardado, sin la foto.
/// <see cref="TaxPercentage"/> es la tasa <b>del producto</b> (spec 2026-11-17 R1), la misma que manda la integración en
/// <see cref="ExternalOrderLine"/>: la efectiva es 0 en un pedido sin IVA y no se podría comparar.</summary>
public sealed record ExternalOrderLineState(
    Guid VariantId, Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage);

/// <summary>
/// Spec 2026-11-14 §3 y §4: lo que la sincronización de ediciones lee de un pedido para decidir si un grupo es conflicto, eco o
/// se aplica, y si el pedido admite el cambio. Sólo lee. <see cref="ShippingCountry"/> es el país de la parte de envío
/// (<c>null</c> es Colombia, como en el dominio, y también sin parte: plan D21).
/// </summary>
public sealed record ExternalOrderEditState(
    Guid OrderId,
    long Version,
    OrderStatus Status,
    bool IsEditable,
    bool IsPrepared,
    bool IsInvoiced,
    OrderPaymentStatus PaymentStatus,
    Guid ClientId,
    string Currency,
    bool IsStorePickup,
    IReadOnlyList<ExternalOrderLineState> Lines,
    ShippingDetails Shipping,
    ExternalShippingParty? ShippingParty,
    string? ShippingCountry,
    string? BuyerNotes,
    string? Notes,
    // Spec 2026-11-17 R7: el IVA congelado del pedido; la integración recalcula sus precios con él.
    bool ChargesVat = true);

/// <summary>
/// Spec 2026-11-14 §4: una edición de la plataforma para aplicar. Cada grupo en <c>null</c> no se toca;
/// <see cref="BuyerNotes"/> sólo cuenta con <see cref="ReplaceBuyerNotes"/>. <see cref="ExpectedVersion"/> es la versión del
/// pedido con la que se planeó: si cambió, <c>concurrency.conflict</c>.
/// </summary>
public sealed record ExternalOrderEditDraft(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    OrderSource Source,
    IReadOnlyList<ExternalOrderLine>? Lines,
    ShippingDetails? Shipping,
    ExternalShippingParty? ShippingParty,
    bool ReplaceBuyerNotes,
    string? BuyerNotes,
    string NotesLinePrefix,
    DateTimeOffset OccurredAt);

/// <summary>
/// Spec 2026-11-14 §5: lo que el asesor corrige en un pedido de la plataforma. <see cref="ShippingParty"/> en <c>null</c> no toca la
/// parte; <see cref="BuyerNotes"/> sólo cuenta con <see cref="ReplaceBuyerNotes"/>. <see cref="MemberId"/> es la membresía que firma el
/// historial y <see cref="SubjectId"/> el sujeto de la auditoría. <see cref="ExpectedVersion"/> es el <c>If-Match</c> del pedido.
/// </summary>
public sealed record ExternalOrderCorrection(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    ExternalShippingParty? ShippingParty,
    bool ReplaceBuyerNotes,
    string? BuyerNotes,
    Guid MemberId,
    Guid SubjectId,
    string NotesLinePrefix,
    DateTimeOffset OccurredAt);

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
    /// comprobantes propios; con ellos, lanza <c>order.order.payment_has_proofs</c>. Un pedido anulado o
    /// facturado lo rechaza con <c>order.order.not_editable</c>; uno preparado o archivado lo recibe, y con el
    /// archivado automático del tenant puede quedar archivado (spec 2026-11-10 §3.2, §3.5).</summary>
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
    /// que empiezan por <paramref name="notesLinePrefix"/>. <paramref name="requestedCityIds"/> son las ciudades que el consumidor
    /// pidió hasta ahora, la de esta llamada incluida: la misma parte en una de ellas es su reintento
    /// (<c>AlreadyApplied</c>); en otra la puso una persona (<c>ShippingPresent</c>). Las razones por las que no se puede viajan
    /// en el resultado; la concurrencia (<c>concurrency.conflict</c>) se propaga y el trabajo reintenta.</summary>
    Task<ExternalShippingOutcome> AttachShippingAsync(
        Guid tenantId,
        Guid orderId,
        ExternalShippingParty shipping,
        string notesLinePrefix,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-26 §4.6: cambia sólo el departamento y la ciudad de la parte de envío, si sigue siendo
    /// <paramref name="expected"/> (la que guardó el consumidor). Si ya sólo difiere en la ciudad y ésa está en
    /// <paramref name="requestedCityIds"/>, es un reintento (<c>AlreadyApplied</c>); en otra la movió una persona
    /// (<c>ShippingChanged</c>). Mismos demás resultados y concurrencia que <see cref="AttachShippingAsync"/>.</summary>
    Task<ExternalShippingOutcome> CorrectShippingCityAsync(
        Guid tenantId,
        Guid orderId,
        ExternalShippingParty expected,
        Guid departmentId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-26 D-C12: el estado de envío del pedido, o <c>null</c> si no existe en ese tenant. Sólo lee y sólo ve
    /// el tenant pedido; no mira el origen (el recorrido llega por la fila del espejo, que ya es de una plataforma).</summary>
    Task<ExternalShippingState?> FindShippingStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-14: el pedido como lo compara la sincronización de ediciones, o <c>null</c> si no existe en ese
    /// tenant. Sólo lee.</summary>
    Task<ExternalOrderEditState?> FindEditStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-14 §4: aplica una edición de la plataforma (<c>Order.ApplyExternalEdit</c>) y devuelve el pedido
    /// como quedó guardado. Con <see cref="ExternalOrderEditDraft.ExpectedVersion"/> distinta de la del pedido lanza
    /// <c>RequestConcurrencyException("concurrency.conflict")</c> sin escribir. Deja una entrada de historial sin autor por grupo
    /// aplicado y la auditoría firmada por la integración. Las reglas del pedido (<c>order.order.not_editable</c>, …) se
    /// propagan como <c>QuotationsDomainException</c>; un pedido que no existe, como <c>ResourceNotFoundException</c>.</summary>
    Task<ExternalOrderEditState> ApplyEditAsync(ExternalOrderEditDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-14 §5: la corrección del asesor (parte de envío y nota del cliente) en un pedido de la plataforma. Corre en la
    /// transacción que ya tenga el contexto (la de Shopify, enrolada por el adaptador de Bootstrapper) y guarda con
    /// <c>SaveChangesAsync</c>: confirmar es del dueño de la transacción. Con <see cref="ExternalOrderCorrection.ExpectedVersion"/>
    /// distinta lanza <c>concurrency.conflict</c> sin escribir. Historial con autor (<c>PartiesChanged</c> «Editó la dirección de
    /// envío.», <c>NotesChanged</c> «Editó la nota del cliente.») y auditoría del sujeto
    /// (<c>quotation.order.shipping_party_changed</c>, <c>quotation.order.buyer_notes_changed</c>). Las reglas del pedido se propagan
    /// como <c>QuotationsDomainException</c>.
    /// </summary>
    Task<ExternalOrderEditState> CorrectByMemberAsync(ExternalOrderCorrection correction, CancellationToken cancellationToken);
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
    ITenantClock tenantClock,
    IQuotationWithholdingLookup withholdingLookup,
    IQuotationSettingsStore settingsStore,
    IOrderVariantCostLookup costLookup)
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
        // Sin cuenta bancaria (spec 2026-11-17, R3/R7) la moneda es la del pedido de origen: la integración la manda en
        // la cuenta del borrador.
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
                companyLookup, draft.TenantId, draft.BillingAccount, draft.BillingAccount.Currency ?? string.Empty,
                cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.billing.account_required", "An order needs a billing company.");

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
        // El día del pedido en la plataforma, en la zona del tenant: decide las tasas y es la «Fecha del pedido» (spec
        // 2026-11-16 §3).
        var orderDate = DocumentDates.LocalDate(
            await tenantClock.GetAsync(draft.TenantId, cancellationToken), draft.OccurredAt);
        // Las tasas del cliente del país de la empresa (spec impuestos por tipo §7.5, D22), a la fecha del pedido en la zona
        // del tenant. Un cliente sin tasas da una lista vacía: el webhook de Shopify no empieza a fallar por esto.
        var withholdings = await OrderCustomerWithholdings.ResolveAsync(
            withholdingLookup,
            draft.TenantId,
            draft.ClientId,
            billingAccount,
            orderDate,
            cancellationToken);
        var header = new OrderHeader(
            draft.ClientId, advisor, billingAccount.Currency, draft.PaymentMethod, IsRetail: false,
            GlobalScaleFloor: null, customer.VatSurplus, withholdings);

        var committed = await KeyedOrderCommit.CommitAsync(
            orderRepository, unitOfWork, numberGenerator, numberingFormats, tenantClock, draft.TenantId, externalKey,
            async (orderNumber, _) =>
            {
                // Un pedido que nace pagado congela todas sus líneas al crearse (§4.3). Se busca acá, dentro de la
                // fábrica, para que un reingreso con clave (la rama Existing) nunca consulte el catálogo.
                var costs = draft.PaymentStatus == OrderPaymentStatus.FullPaymentReceived
                    ? await OrderLineCostsLoader.LoadAsync(
                        costLookup, draft.TenantId, header.Currency,
                        lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken)
                    : OrderLineCosts.None;
                var order = Order.CreateExternal(
                    OrderId.New(), draft.TenantId, orderNumber, header, draft.Parties.ToDomain(), billingAccount, lines,
                    draft.PaymentStatus, draft.Notes, advisor, [], draft.OccurredAt, orderDate, draft.Source, draft.ExternalReference,
                    draft.Shipping ?? ShippingDetails.None, externalKey, draft.ExternalShippingTitle, costs, draft.BuyerNotes);
                // Decisión del owner (spec 2026-10-27 §4): un pedido de una integración se precia siempre con el valor por
                // defecto de su moneda, no con la regla del tenant. Se fija acá, explícito, y OrderPricingRecalculation
                // lo mantiene así cuando el pedido se edita.
                order.UsePriceRounding(PriceRoundingDefaults.For(order.Currency), draft.OccurredAt);
                return order;
            },
            (order, _) =>
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Created, memberId: null,
                    OrderChangeSummary.CreatedFromExternal(draft.Source, draft.ExternalReference), draft.OccurredAt));
                auditPublisher.PublishByIntegration(
                    draft.TenantId, "quotation.order.created", order.Id.ToString(), "success", draft.OccurredAt);
                lifecycleEvents.PublishOrderCreated(order, draft.OccurredAt);
            },
            cancellationToken);
        if (committed is not null)
        {
            return new ExternalOrderResult(
                committed.Id.Value, committed.OrderNumber, committed.Total, committed.NetTotal, committed.Currency,
                committed.ClientId, Shipping: ToExternal(committed.Shipping));
        }

        // Sólo se llega acá si perdió la carrera, que exige la clave.
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
        party is null
            ? null
            : new(
                party.Name, party.Phone, party.Email, party.Address, party.DepartmentId, party.CityId, party.PostalCode,
                party.Country, party.CityName, party.RegionId);

    private static QuotationPartyDetails ToDetails(ExternalShippingParty party) => new()
    {
        Name = party.Name,
        Phone = party.Phone,
        Email = party.Email,
        Address = party.Address,
        DepartmentId = party.DepartmentId,
        CityId = party.CityId,
        PostalCode = party.PostalCode,
        Country = party.Country,
        CityName = party.CityName,
        RegionId = party.RegionId,
    };

    public Task<ExternalShippingOutcome> AttachShippingAsync(
        Guid tenantId,
        Guid orderId,
        ExternalShippingParty shipping,
        string notesLinePrefix,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        return ChangeShippingAsync(
            tenantId, orderId,
            (order, now) => order.AttachExternalShipping(ToDetails(shipping), notesLinePrefix, requestedCityIds, now),
            cancellationToken);
    }

    public Task<ExternalShippingOutcome> CorrectShippingCityAsync(
        Guid tenantId,
        Guid orderId,
        ExternalShippingParty expected,
        Guid departmentId,
        Guid cityId,
        IReadOnlyCollection<Guid> requestedCityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ChangeShippingAsync(
            tenantId, orderId,
            (order, now) => order.CorrectExternalShippingCity(ToDetails(expected), departmentId, cityId, requestedCityIds, now),
            cancellationToken);
    }

    public async Task<ExternalShippingState?> FindShippingStateAsync(
        Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        return order is null
            ? null
            : new ExternalShippingState(order.Status, order.IsStorePickup, ToExternal(order.Shipping), order.Notes, order.IsEditable);
    }

    public async Task<ExternalOrderEditState?> FindEditStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        return order is null ? null : EditState(order);
    }

    /// <summary>
    /// Spec 2026-11-14 §4. La versión se compara contra lo cargado antes de tocar nada, como el <c>If-Match</c> de la edición
    /// manual: la sincronización planeó contra esa versión, y si el asesor editó entre el plan y la escritura gana el asesor (el
    /// cambio queda pendiente). La foto de las variantes nuevas sale del catálogo; el precio no: es el que cobró la plataforma.
    /// </summary>
    public async Task<ExternalOrderEditState> ApplyEditAsync(ExternalOrderEditDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var order = await orderRepository.FindByIdAsync(draft.TenantId, new OrderId(draft.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(draft.OrderId);
        if (order.Version != draft.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed after the edit was planned.");
        }

        if (order.Source != draft.Source)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "The order does not come from the platform that sent the edit.");
        }

        IReadOnlyCollection<OrderLineInput>? lines = null;
        var costs = OrderLineCosts.None;
        if (draft.Lines is { } draftLines)
        {
            var variantIds = draftLines.Select(line => line.VariantId).Distinct().ToArray();
            var products = await productLookup.FindManyAsync(draft.TenantId, variantIds, cancellationToken);
            lines = draftLines
                .Select(line =>
                {
                    products.TryGetValue(line.VariantId, out var product);
                    return new OrderLineInput(
                        line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.TaxPercentage,
                        product is null ? null : new QuotationProductSnapshot(product.Code, product.Name));
                })
                .ToArray();
            // Un pedido pagado congela el costo de las líneas nuevas al entrar (§4.3).
            if (order.PaymentStatus == OrderPaymentStatus.FullPaymentReceived)
            {
                costs = await OrderLineCostsLoader.LoadAsync(costLookup, draft.TenantId, order.Currency, variantIds, cancellationToken);
            }
        }

        var result = order.ApplyExternalEdit(
            new ExternalOrderEdit(
                lines,
                draft.Shipping,
                draft.ShippingParty is null ? null : ToDetails(draft.ShippingParty),
                draft.ReplaceBuyerNotes,
                draft.BuyerNotes,
                draft.NotesLinePrefix),
            costs,
            draft.OccurredAt);
        if (!result.Any)
        {
            return EditState(order);
        }

        // Una entrada por grupo, con el evento de la edición equivalente y sin autor: lo editó la plataforma.
        foreach (var (changed, group, eventType) in new[]
                 {
                     (result.LinesChanged, ExternalOrderEditGroup.Lines, OrderHistoryEventType.ItemsChanged),
                     (result.ShippingChanged, ExternalOrderEditGroup.Shipping, OrderHistoryEventType.PartiesChanged),
                     (result.PartyChanged, ExternalOrderEditGroup.Party, OrderHistoryEventType.PartiesChanged),
                     (result.BuyerNotesChanged, ExternalOrderEditGroup.BuyerNotes, OrderHistoryEventType.NotesChanged),
                 })
        {
            if (changed)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, eventType, memberId: null,
                    OrderChangeSummary.EditedFromExternal(order.Source, group), draft.OccurredAt));
            }
        }

        auditPublisher.PublishByIntegration(
            draft.TenantId, "quotation.order.external_edit_applied", order.Id.ToString(), "success", draft.OccurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EditState(order);
    }

    public async Task<ExternalOrderEditState> CorrectByMemberAsync(ExternalOrderCorrection correction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var order = await orderRepository.FindByIdAsync(correction.TenantId, new OrderId(correction.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(correction.OrderId);
        if (order.Version != correction.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed since it was loaded.");
        }

        // La corrección no recibe código postal (spec 2026-11-24 §5): reescribir la parte conserva el que tenía, o cada
        // corrección lo borraría. Sólo en el mismo departamento: el código es de la provincia, y en otra sería el de otra
        // dirección, que además viajaría a Shopify por encima de su zip.
        var partyChanged = correction.ShippingParty is { } party
            && order.EditExternalShippingParty(
                ToDetails(party) with
                {
                    PostalCode = order.Shipping is { } current && current.DepartmentId == party.DepartmentId
                        ? current.PostalCode
                        : null,
                },
                correction.NotesLinePrefix,
                correction.OccurredAt);
        var notesChanged = correction.ReplaceBuyerNotes
            && order.UpdateExternalBuyerNotes(correction.BuyerNotes, correction.OccurredAt);
        if (!partyChanged && !notesChanged)
        {
            return EditState(order);
        }

        var member = new MemberId(correction.MemberId);
        foreach (var (changed, eventType, summary, action) in new[]
                 {
                     (partyChanged, OrderHistoryEventType.PartiesChanged, OrderChangeSummary.ShippingPartyEdited(),
                         "quotation.order.shipping_party_changed"),
                     (notesChanged, OrderHistoryEventType.NotesChanged, OrderChangeSummary.BuyerNotesEdited(),
                         "quotation.order.buyer_notes_changed"),
                 })
        {
            if (changed)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, eventType, member, summary, correction.OccurredAt));
                auditPublisher.Publish(
                    correction.TenantId, correction.SubjectId, action, order.Id.ToString(), "success", correction.OccurredAt);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EditState(order);
    }

    private static ExternalOrderEditState EditState(Order order) =>
        new(
            order.Id.Value,
            order.Version,
            order.Status,
            order.IsEditable,
            order.IsPrepared,
            order.IsInvoiced,
            order.PaymentStatus,
            order.ClientId,
            order.Currency,
            order.IsStorePickup,
            order.Items
                .OrderBy(item => item.Position)
                // La tasa del producto (R1), no la efectiva: es la que la integración vuelve a mandar en cada edición.
                .Select(item => new ExternalOrderLineState(
                    item.VariantId, item.ProductId, item.Quantity, item.UnitPrice, item.DiscountPercentage, item.ProductTaxRate))
                .ToArray(),
            order.ShippingDetails,
            ToExternal(order.Shipping),
            order.Shipping?.Country,
            order.BuyerNotes,
            order.Notes,
            order.ChargesVat);

    // Sin auditoría ni historial acá (desvío S9): la entrada la escribe el consumidor, que sabe el sinónimo y el operador.
    // Un código del dominio que no es de los rechazos conocidos se propaga.
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
        ExternalOrderErrors.NotEditable => ExternalShippingOutcomeKind.NotPending,
        ExternalOrderErrors.SourceInvalid => ExternalShippingOutcomeKind.NotExternal,
        ExternalOrderErrors.StorePickup => ExternalShippingOutcomeKind.StorePickup,
        ExternalOrderErrors.ShippingPartyPresent => ExternalShippingOutcomeKind.ShippingPresent,
        ExternalOrderErrors.ShippingPartyChanged => ExternalShippingOutcomeKind.ShippingChanged,
        ExternalOrderErrors.ShippingPartyIncomplete => ExternalShippingOutcomeKind.PartyIncomplete,
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

        // Spec 2026-11-10 §3.2 y §3.5: el pago de la plataforma también llega a un pedido preparado, y con el ajuste del
        // tenant, quedar pagado y preparado lo archiva. Lo archiva la integración, no una persona: historial sin autor y
        // auditoría firmada por la integración.
        var settings = await settingsStore.GetOrderSettingsAsync(tenantId, cancellationToken);
        // Hacia pagado congela lo que todavía no congeló (§4.3); hacia otro estado no hay nada que buscar.
        var costs = status == OrderPaymentStatus.FullPaymentReceived
            ? await OrderLineCostsLoader.LoadAsync(
                costLookup, tenantId, order.Currency, order.UncostedVariantIds, cancellationToken)
            : OrderLineCosts.None;
        var archived = order.SyncExternalPaymentStatus(status, settings.AutoArchive, costs, now);
        auditPublisher.PublishByIntegration(
            tenantId, "quotation.order.payment_status_synced", order.Id.ToString(), "success", now);
        if (archived)
        {
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Archived, memberId: null,
                OrderChangeSummary.ArchivedAutomatically(), now));
            auditPublisher.PublishByIntegration(
                tenantId, OrderAutoArchive.AuditAction, order.Id.ToString(), "success", now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// La anulación que llega de la plataforma (spec 2026-11-10 §3.1): gana sobre preparado y archivado
    /// (<see cref="Order.CancelFromPlatform"/>). Con el pedido preparado, el historial avisa que hay un paquete
    /// despachado que recuperar. Facturado sigue lanzando <c>order.order.already_invoiced</c>.
    /// </summary>
    public async Task CancelAsync(
        Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        var by = new MemberId(cancelledByMembershipId);
        order.CancelFromPlatform(by, reason, now);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, by,
            order.IsPrepared
                ? OrderChangeSummary.CancelledWhilePrepared(order.CancellationReason!)
                : OrderChangeSummary.Cancelled(order.CancellationReason!),
            now));
        auditPublisher.PublishByIntegration(tenantId, "quotation.order.cancelled", order.Id.ToString(), "success", now);
        lifecycleEvents.PublishOrderCancelled(order, fromPlatform: true, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
