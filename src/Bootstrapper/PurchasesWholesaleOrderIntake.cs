using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Quotations y Wholesale → Purchases (mayorista fase 3): «Enviar» una orden vinculada crea el pedido en el tenant mayorista.
/// <list type="bullet">
/// <item><b>Scope propio por llamada</b>, como <see cref="StoreOrdersAdapter"/>: el motor de pedidos puede limpiar el rastreo de
/// <c>QuotationsDbContext</c> ante una carrera de claves, y en el scope del llamador se llevaría lo que otro dejó pendiente.</item>
/// <item>D-8: primero busca el pedido que ya salió de esta orden (<see cref="IWholesaleOrderGateway.FindAsync"/>, con el comprador
/// en la clave, ruling X1) y lo devuelve sin revalidar relación ni precio. Base y descuento salen de la oferta vigente sin mirar la
/// relación, sólo si su precio es el del pedido; si no, el precio hace de base, sin descuento.</item>
/// <item>Si no existe: términos y precios de la relación (403/422 de Wholesale tal cual), asesor del distribuidor si su membresía
/// sigue activa y si no el owner del mayorista (D-14), y el pedido al precio de lista.</item>
/// <item>D-15: un rechazo del motor de pedidos es 409 <c>purchases.order.wholesaler_rejected</c>; el código interno
/// <c>quotation.*</c>/<c>order.*</c> del otro tenant va sólo al log.</item>
/// <item>Revisión del bloque B, M2: la moneda del pedido tiene que ser la de la orden. Si el mayorista la cambió, nada se crea ni
/// se adopta: 409 <c>wholesaler_rejected</c>, y el distribuidor edita la orden para volver a preciarla.</item>
/// <item>Cada línea vuelve como foto de la orden con el costo sin IVA (D-9); una sin línea de origen (agregada por el mayorista
/// antes del reintento) recibe un id nuevo.</item>
/// </list>
/// </summary>
internal sealed partial class PurchasesWholesaleOrderIntake(
    IServiceScopeFactory scopes,
    ILogger<PurchasesWholesaleOrderIntake> logger) : IWholesaleOrderIntake
{
    public async Task<WholesaleIntakeResult> SubmitAsync(WholesaleIntakeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IWholesaleOrderGateway>();
        var directory = scope.ServiceProvider.GetRequiredService<IWholesaleIntakeDirectory>();
        var variantIds = request.Lines.Select(line => line.VariantId).Distinct().ToArray();

        if (await gateway.FindAsync(request.WholesalerTenantId, request.BuyerTenantId, request.PurchaseOrderId, cancellationToken)
            is { } existing)
        {
            EnsureCurrency(request, existing.Currency);
            var current = await directory.OffersForOrderAsync(
                request.WholesalerTenantId, request.BuyerTenantId, existing.Currency, variantIds, cancellationToken);
            // D-14: el reintento usa el flag vivo de la lista; sin lista o sin relación, false (sin precio hasta la aprobación).
            return ToIntake(existing, current.Offers, current.ShowPrices);
        }

        var (terms, offers) = await directory.PriceVisibleAsync(
            request.WholesalerTenantId, request.BuyerTenantId, variantIds, cancellationToken);
        EnsureCurrency(request, terms.Currency);
        var advisor = await directory.IsActiveMemberAsync(request.WholesalerTenantId, terms.AdvisorMembershipId, cancellationToken)
            ? terms.AdvisorMembershipId
            : await directory.FindOwnerMembershipIdAsync(request.WholesalerTenantId, cancellationToken);

        var draft = new WholesaleOrderDraft(
            request.WholesalerTenantId,
            request.BuyerTenantId,
            request.PurchaseOrderId,
            request.PurchaseOrderNumber,
            terms.CustomerId,
            advisor,
            terms.BillingCompanyId,
            terms.Currency,
            request.Notes,
            [.. request.Lines.Select(line => new WholesaleOrderLine(
                line.PurchaseOrderLineId, line.VariantId, line.Quantity, offers[line.VariantId].ListPrice))]);

        WholesaleOrderResult created;
        try
        {
            created = await gateway.CreateAsync(draft, cancellationToken);
        }
        catch (QuotationsDomainException exception)
        {
            LogRejected(logger, request.WholesalerTenantId, request.BuyerTenantId, request.PurchaseOrderId, exception.Code);
            throw new ResourceConflictException(
                PurchasesErrorCodes.WholesalerRejected,
                "El mayorista no pudo recibir el pedido. Escríbele o intenta de nuevo más tarde.",
                exception);
        }

        // Una carrera perdida devuelve el pedido del ganador, que pudo salir con otra moneda.
        EnsureCurrency(request, created.Currency);
        return ToIntake(created, offers, terms.ShowPrices);
    }

    public async Task CancelAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber,
        Guid? orderId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IWholesaleOrderGateway>();

        // M1: un borrador no conoce su pedido; si el envío alcanzó a crearlo, se encuentra por la clave de esta orden.
        var target = orderId
            ?? (await gateway.FindAsync(wholesalerTenantId, buyerTenantId, purchaseOrderId, cancellationToken))?.OrderId;
        if (target is not { } id)
        {
            return;
        }

        try
        {
            await gateway.CancelByBuyerAsync(
                wholesalerTenantId, id, buyerTenantId, purchaseOrderId, purchaseOrderNumber, cancellationToken);
        }
        catch (QuotationsDomainException exception)
        {
            LogCancelRefused(logger, wholesalerTenantId, buyerTenantId, purchaseOrderId, exception.Code);
            throw new ResourceConflictException(
                PurchasesErrorCodes.NotCancellable,
                "El mayorista ya aprobó o empezó a preparar el pedido; la orden ya no se puede cancelar.",
                exception);
        }
    }

    private void EnsureCurrency(WholesaleIntakeRequest request, string orderCurrency)
    {
        if (string.Equals(request.Currency, orderCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LogCurrencyMismatch(
            logger, request.WholesalerTenantId, request.BuyerTenantId, request.PurchaseOrderId, orderCurrency, request.Currency);
        throw new ResourceConflictException(
            PurchasesErrorCodes.WholesalerRejected,
            "El mayorista cambió su moneda desde que armaste la orden. Edítala para volver a preciarla.");
    }

    // Las líneas siguen trayendo el precio (es la verdad del mayorista, y el pedido siempre se crea a precio de lista); quien
    // lo descarta con la lista oculta es el dominio de Compras, no este adaptador.
    private static WholesaleIntakeResult ToIntake(
        WholesaleOrderResult order, IReadOnlyDictionary<Guid, WholesaleOffer> offers, bool showPrices) =>
        new(
            order.OrderId,
            order.OrderNumber,
            order.Currency,
            [.. order.Lines.Select(line =>
            {
                var (basePrice, discount) = offers.TryGetValue(line.VariantId, out var offer) && offer.ListPrice == line.UnitPrice
                    ? (offer.BasePrice, offer.DiscountPercent)
                    : (line.UnitPrice, 0m);
                return new LinkedLineSnapshot(
                    line.PurchaseOrderLineId ?? Guid.CreateVersion7(),
                    line.VariantId,
                    line.Code,
                    line.Name,
                    line.Quantity,
                    PurchaseOrderMath.NetOfVat(line.UnitPrice, line.TaxPercentage),
                    line.TaxPercentage,
                    basePrice,
                    discount,
                    line.UnitPrice);
            })],
            order.AlreadyExisted,
            showPrices);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The order engine of wholesaler {WholesalerTenantId} rejected purchase order {PurchaseOrderId} of buyer {BuyerTenantId} with code {Code}")]
    private static partial void LogRejected(
        ILogger logger, Guid wholesalerTenantId, Guid buyerTenantId, Guid purchaseOrderId, string code);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Wholesaler {WholesalerTenantId} refused to cancel the order of purchase order {PurchaseOrderId} of buyer {BuyerTenantId} with code {Code}")]
    private static partial void LogCancelRefused(
        ILogger logger, Guid wholesalerTenantId, Guid buyerTenantId, Guid purchaseOrderId, string code);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Wholesaler {WholesalerTenantId} prices in {WholesalerCurrency} but purchase order {PurchaseOrderId} of buyer {BuyerTenantId} is in {OrderCurrency}; the send is rejected")]
    private static partial void LogCurrencyMismatch(
        ILogger logger, Guid wholesalerTenantId, Guid buyerTenantId, Guid purchaseOrderId, string wholesalerCurrency, string orderCurrency);
}
