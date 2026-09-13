using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// SALE-01: el listado operativo de ventas del tenant.
///
/// Ruta propia del módulo y no una vista del reporte de ventas: Reporting es lectura analítica
/// —agrega, exporta, y su filtro no conoce ni el estado de la venta ni su número—, mientras que
/// ésta es la pantalla desde la que se trabaja. Los dos coexisten a propósito.
/// </summary>
public sealed record ListSalesQuery(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    /// <summary>Sobre <c>Sale.ConvertedAt</c>, que es la fecha de la venta. Inclusive las dos
    /// puntas: "hasta el 30" incluye todo el 30.</summary>
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    /// <summary>Texto libre contra el CUC del cliente. Ni la venta ni la cotización lo guardan,
    /// así que el handler lo resuelve a ids contra Customers antes de filtrar — mismo camino que
    /// el filtro por NIT en <see cref="ListQuotationsQuery"/>.</summary>
    string? ClientCuc,
    string? SaleNumber,
    int Page,
    int PageSize) : IQuery<SalePage>;

/// <summary>
/// Fila del listado. La venta es 1:1 con su cotización y no repite cliente, asesora, moneda ni
/// totales, así que la fila los trae ya resueltos: la alternativa es un GET por fila contra la
/// cotización, y otro contra Customers para poner un nombre.
/// </summary>
public sealed record SaleListItemDto(
    Guid Id,
    string SaleNumber,
    Guid QuotationId,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Null si el cliente ya no existe — referencia blanda entre módulos, mismo criterio
    /// que <see cref="QuotationListItemDto.ClientName"/>.</summary>
    string? ClientName,
    Guid AdvisorId,
    string? AdvisorEmail,
    string Status,
    string PaymentStatus,
    /// <summary>La forma de pago de la cotización de origen. Viene null en todo lo creado desde
    /// que el editor dejó de pedirla; la columna vacía es ese hecho, no una falla de esta
    /// consulta.</summary>
    string? PaymentMethod,
    DateTimeOffset ConvertedAt,
    string Currency,
    decimal Total);

public sealed record SalePage(
    IReadOnlyList<SaleListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed class ListSalesHandler(
    ISaleRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListSalesQuery, SalePage>
{
    public async Task<SalePage> HandleAsync(
        ListSalesQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SalesPermissions.SaleRead);

        // Mismo tope y mismo default que el listado de cotizaciones. Dos clases de paginado con
        // los mismos números en el mismo módulo serían dos lugares para desincronizar.
        var page = QuotationPaging.NormalizePage(query.Page);
        var pageSize = QuotationPaging.NormalizePageSize(query.PageSize);
        var status = SaleListing.ParseStatus(query.Status);
        var paymentStatus = SaleListing.ParsePaymentStatus(query.PaymentStatus);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await SaleListing.ResolveClientIdsByCucAsync(
            customerLookup, query.TenantId, query.ClientCuc, cancellationToken);

        var (rows, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            query.ConvertedFrom,
            query.ConvertedTo,
            query.SaleNumber,
            page,
            pageSize,
            cancellationToken);

        var items = await SaleListing.ToListItemsAsync(
            customerLookup, advisorLookup, query.TenantId, rows, cancellationToken);

        return new SalePage(items, total, page, pageSize);
    }
}
