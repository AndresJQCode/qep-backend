using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper;

/// <summary>
/// Quotations → Store por <see cref="IStoreOrderGateway"/>. Los totales salen siempre del motor de pedidos.
/// <para><b>Cada llamada resuelve el gateway en un scope propio</b>, como <see cref="ShopifyOrderWriter"/>: el motor puede limpiar
/// el tracker de <c>QuotationsDbContext</c> ante una carrera de claves, y en el scope del llamador eso se llevaría lo que otro
/// adaptador dejó pendiente.</para>
/// </summary>
internal sealed class StoreOrdersAdapter(IServiceScopeFactory scopes) : IStoreOrders
{
    // Todo rechazo del motor (línea inexistente, inactiva, cantidad que la escala no admite, u otro) es para la tienda una línea que
    // no se puede vender: el código interno quotation.* nunca llega al visitante anónimo.

    public async Task<StoreQuoteView> QuoteAsync(
        Guid tenantId,
        string currency,
        StorePricingMode mode,
        IReadOnlyList<(Guid VariantId, int Quantity)> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        var request = new StoreQuoteRequest(
            tenantId,
            currency,
            mode == StorePricingMode.RetailOnly,
            lines.Select(line => new StoreQuoteLine(line.VariantId, line.Quantity)).ToList());
        try
        {
            var quote = await gateway.QuoteAsync(request, cancellationToken);
            return new StoreQuoteView(
                quote.Currency,
                quote.Lines.Select(line => new StoreQuoteLineView(
                    line.VariantId,
                    line.ProductId,
                    line.Name,
                    line.Quantity,
                    line.UnitPrice,
                    line.DiscountPercentage,
                    line.DiscountedUnitPrice,
                    line.LineTotal,
                    line.DiscountOrigin)).ToList(),
                quote.Subtotal,
                quote.DiscountAmount,
                quote.TaxAmount,
                quote.Total);
        }
        catch (QuotationsDomainException)
        {
            throw new StoreDomainException(
                StoreErrorCodes.LineUnavailable, "A line of the cart cannot be sold.");
        }
    }
}
