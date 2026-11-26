using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary><c>Status</c> llega como texto para que un valor raro sea un 422 con <c>errors.Status</c> y no un 400 del binding.</summary>
public sealed record ListPurchaseOrdersQuery(
    Guid TenantId,
    string? Status,
    Guid? SupplierId,
    string? Search,
    int Page,
    int PageSize) : IQuery<PurchaseOrdersResponse>;

/// <summary>
/// D-12: <c>status</c> nulo o vacío es «todos»; si viene, tiene que ser el nombre de un estado, sin importar mayúsculas. Se
/// compara con los nombres y no con <c>Enum.TryParse</c> a secas, que aceptaría <c>"1"</c> o <c>"99"</c>.
/// </summary>
public sealed class ListPurchaseOrdersValidator : AbstractValidator<ListPurchaseOrdersQuery>
{
    public ListPurchaseOrdersValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => string.IsNullOrWhiteSpace(status) || PurchaseOrderStatusFilter.TryParse(status, out _))
            .WithMessage("Status must be one of Draft, Sent, Confirmed, Received or Cancelled.");
    }
}

internal static class PurchaseOrderStatusFilter
{
    public static bool TryParse(string? value, out PurchaseOrderStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        foreach (var candidate in Enum.GetValues<PurchaseOrderStatus>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// La lista con sus pestañas. <c>search</c> busca en el número o en el nombre del proveedor (D-11: los ids salen del directorio,
/// con tope 200); <c>Counts</c> trae los cinco estados más <c>all</c> con los mismos filtros salvo <c>status</c> (D-18).
/// </summary>
public sealed class ListPurchaseOrdersHandler(
    IExecutionContext executionContext,
    IPurchaseOrderRepository orders,
    IPurchasesSupplierLookup suppliers,
    PurchaseOrderComposer composer,
    IValidator<ListPurchaseOrdersQuery> validator)
    : IQueryHandler<ListPurchaseOrdersQuery, PurchaseOrdersResponse>
{
    internal const int SupplierSearchLimit = 200;

    public async Task<PurchaseOrdersResponse> HandleAsync(ListPurchaseOrdersQuery query, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, query.TenantId, PurchasesPermissions.OrderRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!PurchaseOrderStatusFilter.TryParse(query.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a purchase order status.");
        }

        var page = PurchaseOrderPaging.NormalizePage(query.Page);
        var pageSize = PurchaseOrderPaging.NormalizePageSize(query.PageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        IReadOnlyCollection<Guid>? supplierIds = search is null
            ? null
            : await suppliers.SearchIdsAsync(query.TenantId, search, SupplierSearchLimit, cancellationToken);

        var (items, total) = await orders.SearchAsync(
            query.TenantId, status, query.SupplierId, search, supplierIds, page, pageSize, cancellationToken);
        var counts = await orders.CountByStatusAsync(
            query.TenantId, query.SupplierId, search, supplierIds, cancellationToken);
        var summaries = await composer.SummariesAsync(query.TenantId, items, cancellationToken);

        return new PurchaseOrdersResponse(summaries, total, page, pageSize, ToCounts(counts));
    }

    private static PurchaseOrderStatusCountsDto ToCounts(IReadOnlyDictionary<PurchaseOrderStatus, int> counts)
    {
        int CountOf(PurchaseOrderStatus status) => counts.TryGetValue(status, out var count) ? count : 0;

        var draft = CountOf(PurchaseOrderStatus.Draft);
        var sent = CountOf(PurchaseOrderStatus.Sent);
        var confirmed = CountOf(PurchaseOrderStatus.Confirmed);
        var received = CountOf(PurchaseOrderStatus.Received);
        var cancelled = CountOf(PurchaseOrderStatus.Cancelled);
        return new PurchaseOrderStatusCountsDto(
            draft, sent, confirmed, received, cancelled, draft + sent + confirmed + received + cancelled);
    }
}
