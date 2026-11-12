using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Buyers → Quotations por <see cref="IBuyerOrderGateway"/>. <b>Cada llamada resuelve el gateway en un scope propio</b>, como
/// <see cref="StoreOrdersAdapter"/>: el gateway puede limpiar el tracker de <c>QuotationsDbContext</c> ante una carrera, y en el scope
/// del llamador eso se llevaría lo que otro adaptador dejó pendiente. Los records se copian campo a campo dentro del scope.
/// </summary>
internal sealed class BuyerOrdersAdapter(IServiceScopeFactory scopes) : IBuyerOrders
{
    public async Task<BuyerOrderPageDto> ListAsync(
        Guid tenantId, Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IBuyerOrderGateway>();
        var result = await gateway.ListAsync(tenantId, accountId, page, pageSize, cancellationToken);
        return new BuyerOrderPageDto(result.Items.Select(ToSummary).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<BuyerOrderDetailDto?> FindAsync(
        Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IBuyerOrderGateway>();
        return await gateway.FindAsync(tenantId, accountId, orderId, cancellationToken) is { } detail ? ToDetail(detail) : null;
    }

    public async Task<BuyerOrderDetailDto?> CancelAsync(
        Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IBuyerOrderGateway>();
        return await gateway.CancelAsync(tenantId, accountId, orderId, cancellationToken) is { } detail ? ToDetail(detail) : null;
    }

    private static BuyerOrderSummaryDto ToSummary(BuyerOrderSummary order) =>
        new(
            order.Id, order.Number, order.CreatedAt, order.Status, order.PaymentStatus, order.FulfillmentStatus, order.Invoiced,
            order.Total, order.Currency, order.LineCount, order.FirstLineName, order.CanCancel, order.IsStorePickup);

    private static BuyerOrderDetailDto ToDetail(BuyerOrderDetail order) =>
        new(
            order.Id, order.Number, order.CreatedAt, order.Status, order.PaymentStatus, order.FulfillmentStatus, order.Invoiced,
            order.Total, order.Currency, order.LineCount, order.FirstLineName,
            order.Lines.Select(line => new BuyerOrderLineDto(line.Name, line.Quantity, line.UnitPrice, line.LineTotal)).ToList(),
            order.CancelledAt, order.CanCancel,
            order.Subtotal, order.TaxAmount, order.FreightTotal, order.IsStorePickup, order.CarrierName, order.BilledTo,
            order.Billing is { } billing
                ? new BuyerOrderBillingDto(
                    billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Email, billing.Phone,
                    billing.Address, billing.Country, billing.DepartmentName, billing.CityName)
                : null,
            order.Shipping is { } shipping
                ? new BuyerOrderShippingDto(
                    shipping.Name, shipping.Phone, shipping.Address, shipping.Country, shipping.DepartmentName, shipping.CityName)
                : null,
            order.BuyerNotes);
}
