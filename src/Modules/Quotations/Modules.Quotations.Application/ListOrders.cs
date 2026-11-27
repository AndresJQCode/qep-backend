using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Spec 2026-10-15 §6.1: cliente, CUC y asesor filtran columnas del pedido; <c>Source</c>
/// es el filtro nuevo por origen (sin valor, todos). Spec 2026-11-10 §5: sin <c>Status</c> no salen los
/// archivados; <c>FulfillmentStatus</c> (<c>NotPrepared</c>/<c>Prepared</c>) e <c>Invoiced</c> (<c>true</c>/<c>false</c>)
/// van al final para no correr los posicionales de quien ya construye la consulta.</summary>
public sealed record ListOrdersQuery(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? ClientCuc,
    string? OrderNumber,
    string? Source,
    int Page,
    int PageSize,
    /// <summary>La zona IANA de quien filtra. Se acepta por contrato pero ya no mueve el filtro: las fechas cortan por la
    /// fecha del pedido, que es un día del tenant (spec 2026-11-16 §4).</summary>
    string? TimeZone = null,
    string? FulfillmentStatus = null,
    string? Invoiced = null,
    /// <summary>Mayorista fase 3: <c>Pending</c> o <c>Approved</c> (sin distinguir mayúsculas); otro valor es 422
    /// <c>validation.failed</c> con <c>errors.WholesaleApproval</c>.</summary>
    string? WholesaleApproval = null,
    /// <summary>Spec 2026-11-26 §6.6: <c>Standard</c>, <c>Project</c> o <c>All</c> (sin distinguir mayúsculas); sin valor,
    /// todos. Otro valor es 422 <c>validation.failed</c> con <c>errors.kind</c>.</summary>
    string? Kind = null) : IQuery<OrderPage>;

/// <summary>Fila de la lista. <c>QuotationId</c> y <c>QuotationNumber</c> son nulos en un pedido manual
/// o externo; <c>Source</c> y <c>ExternalReference</c> dicen de dónde vino. <c>FulfillmentStatus</c> e
/// <c>Invoiced</c> están para que la tabla pinte las columnas de envío y facturado sin pedir el detalle
/// (spec 2026-11-10 §5).</summary>
public sealed record OrderListItemDto(
    Guid Id,
    string OrderNumber,
    Guid? QuotationId,
    string? QuotationNumber,
    Guid ClientId,
    string? ClientName,
    Guid AdvisorId,
    string? AdvisorName,
    string Status,
    string PaymentStatus,
    string FulfillmentStatus,
    bool Invoiced,
    string? PaymentMethod,
    DateTimeOffset ConvertedAt,
    string Currency,
    decimal Total,
    string Source,
    string? ExternalReference,
    /// <summary>La entrega por envío, misma forma que en el detalle (ver <see cref="ShippingDto"/>).</summary>
    ShippingDto Shipping,
    /// <summary>La fecha del pedido (spec 2026-11-16 §4).</summary>
    DateOnly OrderDate,
    /// <summary>Ver <see cref="Order.RequiresShipping"/> (spec 2026-11-18 §5). Resuelto por página, porque la fila no trae
    /// las líneas.</summary>
    bool RequiresShipping,
    /// <summary>Mayorista fase 3: número de la orden y aprobación; null fuera de un pedido mayorista.</summary>
    OrderListWholesaleDto? Wholesale = null,
    /// <summary>Ver <see cref="OrderDto.Kind"/>.</summary>
    string Kind = "Standard");

/// <summary><c>PendingWholesaleApprovals</c>: el aviso «N pedidos de distribuidores esperan tu aprobación» (adenda), con los
/// filtros vigentes salvo el de estado.</summary>
public sealed record OrderPage(
    IReadOnlyList<OrderListItemDto> Items,
    int Total,
    int Page,
    int PageSize,
    int PendingWholesaleApprovals = 0);

public sealed class ListOrdersHandler(
    IOrderRepository repository,
    IQuotationRepository quotationRepository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListOrdersQuery, OrderPage>
{
    public async Task<OrderPage> HandleAsync(
        ListOrdersQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderRead);

        // Mismo tope y mismo default que el listado de cotizaciones. Dos clases de paginado con
        // los mismos números en el mismo módulo serían dos lugares para desincronizar.
        var page = QuotationPaging.NormalizePage(query.Page);
        var pageSize = QuotationPaging.NormalizePageSize(query.PageSize);
        var status = OrderListing.ParseStatus(query.Status);
        var paymentStatus = OrderListing.ParsePaymentStatus(query.PaymentStatus);
        var fulfillmentStatus = OrderListing.ParseFulfillmentStatus(query.FulfillmentStatus);
        var invoiced = OrderListing.ParseInvoiced(query.Invoiced);
        var source = OrderListing.ParseSource(query.Source);
        var wholesaleApproved = OrderListing.ParseWholesaleApproval(query.WholesaleApproval);
        var kind = OrderListing.ParseKind(query.Kind);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, query.TenantId, query.ClientCuc, cancellationToken);

        // Spec 2026-11-16 §4: convertedFrom/convertedTo conservan su nombre pero cortan por la fecha del pedido, que ya es
        // un día del tenant: no hay huso que aplicar, así que TimeZone se sigue aceptando y no se usa. Se valida sólo con un
        // rango, como antes (spec 2026-09-17): sin fechas la zona nunca se miró y una lista simple no responde 422 por ella.
        if (query.ConvertedFrom is not null || query.ConvertedTo is not null)
        {
            OrderListing.EnsureValidTimeZone(query.TimeZone);
        }

        var (rows, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            fulfillmentStatus,
            invoiced,
            query.ConvertedFrom,
            query.ConvertedTo,
            query.OrderNumber,
            source,
            kind,
            wholesaleApproved,
            page,
            pageSize,
            cancellationToken);
        var pendingWholesaleApprovals = await repository.CountAwaitingWholesaleApprovalAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            paymentStatus,
            fulfillmentStatus,
            invoiced,
            query.ConvertedFrom,
            query.ConvertedTo,
            query.OrderNumber,
            source,
            kind,
            cancellationToken);

        var items = await OrderListing.ToListItemsAsync(
            customerLookup, advisorLookup, quotationRepository, repository, query.TenantId, rows, cancellationToken);
        return new OrderPage(items, total, page, pageSize, pendingWholesaleApprovals);
    }
}
