using BuildingBlocks.Application;
using FluentValidation;

namespace Modules.Buyers.Application;

public sealed record BuyerOrderLineDto(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Contrato con <c>qep-storefront</c>. Los cuatro ejes del pedido viajan por nombre (spec 2026-11-10 §9):
/// <c>Status</c> (<c>Open</c> | <c>Archived</c> | <c>Cancelled</c>), <c>PaymentStatus</c>, <c>FulfillmentStatus</c>
/// (<c>NotPrepared</c> | <c>Prepared</c>) e <c>Invoiced</c>.</summary>
public sealed record BuyerOrderSummaryDto(
    Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    string FulfillmentStatus, bool Invoiced,
    decimal Total, string Currency, int LineCount, string FirstLineName, bool CanCancel);

public sealed record BuyerOrderDetailDto(
    Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    string FulfillmentStatus, bool Invoiced,
    decimal Total, string Currency, int LineCount, string FirstLineName,
    IReadOnlyList<BuyerOrderLineDto> Lines, DateTimeOffset? CancelledAt, bool CanCancel);

public sealed record BuyerOrderPageDto(IReadOnlyList<BuyerOrderSummaryDto> Items, int Page, int PageSize, int Total);

/// <summary>Buyers → Quotations, sin verlo: el adaptador vive en Bootstrapper. <c>null</c> = no existe para ese comprador en ese tenant.</summary>
public interface IBuyerOrders
{
    Task<BuyerOrderPageDto> ListAsync(Guid tenantId, Guid accountId, int page, int pageSize, CancellationToken cancellationToken);

    Task<BuyerOrderDetailDto?> FindAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken);

    Task<BuyerOrderDetailDto?> CancelAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken);
}

public sealed record ListBuyerOrdersQuery(string Host, string? Token, int Page, int PageSize) : IQuery<BuyerOrderPageDto>;

public sealed class ListBuyerOrdersQueryValidator : AbstractValidator<ListBuyerOrdersQuery>
{
    public const int MaxPageSize = 50;

    public ListBuyerOrdersQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

public sealed record GetBuyerOrderQuery(string Host, string? Token, Guid OrderId) : IQuery<BuyerOrderDetailDto>;

public sealed record CancelBuyerOrderCommand(string Host, string? Token, Guid OrderId) : ICommand<BuyerOrderDetailDto>;

internal static class BuyerOrderErrors
{
    public static ResourceNotFoundException NotFound() => new("order.order.not_found", "The order was not found.");
}

/// <summary>Orden: host (404) → paginación (422) → sesión (401) → pedidos del comprador de la sesión en el tenant de la tienda.</summary>
public sealed class ListBuyerOrdersHandler(
    IBuyerStorefronts storefronts, BuyerSessions sessions, IBuyerOrders orders, IValidator<ListBuyerOrdersQuery> validator)
    : IQueryHandler<ListBuyerOrdersQuery, BuyerOrderPageDto>
{
    public async Task<BuyerOrderPageDto> HandleAsync(ListBuyerOrdersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var storefront = await storefronts.ResolveAsync(query.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, query.Token, cancellationToken);
        return await orders.ListAsync(storefront.TenantId, session.AccountId, query.Page, query.PageSize, cancellationToken);
    }
}

public sealed class GetBuyerOrderHandler(IBuyerStorefronts storefronts, BuyerSessions sessions, IBuyerOrders orders)
    : IQueryHandler<GetBuyerOrderQuery, BuyerOrderDetailDto>
{
    public async Task<BuyerOrderDetailDto> HandleAsync(GetBuyerOrderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var storefront = await storefronts.ResolveAsync(query.Host, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, query.Token, cancellationToken);
        return await orders.FindAsync(storefront.TenantId, session.AccountId, query.OrderId, cancellationToken)
            ?? throw BuyerOrderErrors.NotFound();
    }
}

public sealed class CancelBuyerOrderHandler(IBuyerStorefronts storefronts, BuyerSessions sessions, IBuyerOrders orders)
    : ICommandHandler<CancelBuyerOrderCommand, BuyerOrderDetailDto>
{
    public async Task<BuyerOrderDetailDto> HandleAsync(CancelBuyerOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var storefront = await storefronts.ResolveAsync(command.Host, cancellationToken);
        var session = await sessions.RequireAsync(storefront.Host, command.Token, cancellationToken);
        return await orders.CancelAsync(storefront.TenantId, session.AccountId, command.OrderId, cancellationToken)
            ?? throw BuyerOrderErrors.NotFound();
    }
}
