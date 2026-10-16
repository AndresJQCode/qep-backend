using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Spec 2026-10-15 §6.1: cliente, CUC y asesor filtran columnas del pedido; <c>Source</c>
/// es el filtro nuevo por origen (sin valor, todos).</summary>
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
    /// <summary>La zona IANA de quien filtra (<see cref="OrderListing.CalendarFor"/>). Sin ella, los
    /// días son los del tenant.</summary>
    string? TimeZone = null) : IQuery<OrderPage>;

/// <summary>Fila de la lista. <c>QuotationId</c> y <c>QuotationNumber</c> son nulos en un pedido manual
/// o externo; <c>Source</c> y <c>ExternalReference</c> dicen de dónde vino.</summary>
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
    string? PaymentMethod,
    DateTimeOffset ConvertedAt,
    string Currency,
    decimal Total,
    string Source,
    string? ExternalReference);

public sealed record OrderPage(
    IReadOnlyList<OrderListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed class ListOrdersHandler(
    IOrderRepository repository,
    IQuotationRepository quotationRepository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
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
        var source = OrderListing.ParseSource(query.Source);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, query.TenantId, query.ClientCuc, cancellationToken);

        // El rango se corta en el día de quien filtra, o en el del tenant si no manda su zona (spec
        // 2026-09-17, punto 3). Sin fechas no se consulta el huso.
        var converted = query.ConvertedFrom is null && query.ConvertedTo is null
            ? new TenantInstantRange(null, null)
            : TenantDayRange.Of(
                OrderListing.CalendarFor(await tenantClock.GetAsync(query.TenantId, cancellationToken), query.TimeZone),
                query.ConvertedFrom,
                query.ConvertedTo);

        var (rows, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            converted.From,
            converted.Before,
            query.OrderNumber,
            source,
            page,
            pageSize,
            cancellationToken);

        var items = await OrderListing.ToListItemsAsync(
            customerLookup, advisorLookup, quotationRepository, query.TenantId, rows, cancellationToken);
        return new OrderPage(items, total, page, pageSize);
    }
}
