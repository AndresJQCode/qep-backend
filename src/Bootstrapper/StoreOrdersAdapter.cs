using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;
using StoreOrderDraft = Modules.Store.Application.StoreOrderDraft;

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

    public async Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        return await gateway.FindExistingAsync(tenantId, requestId, cancellationToken) is { } existing ? ToCreated(existing) : null;
    }

    // A diferencia de la cotización, acá los errores del motor NO se esconden: quien toma la solicitud es un asesor, y
    // quotation.item.product_inactive o product_price_unavailable le dicen qué arreglar.
    public async Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        var result = await gateway.CreateAsync(
            new Modules.Quotations.Application.StoreOrderDraft(
                draft.TenantId,
                draft.RequestId,
                draft.RequestNumber,
                draft.ClientId,
                draft.AdvisorMembershipId,
                draft.IsRetail,
                new QuotationBillingAccountRequest(
                    draft.BillingAccount.CompanyId,
                    draft.BillingAccount.BankName,
                    draft.BillingAccount.AccountNumber,
                    draft.BillingAccount.Currency),
                ToParty(draft.Billing),
                ToParty(draft.Shipping),
                draft.BillingWithRetention,
                draft.BillingVatSurplus,
                draft.Lines.Select(line => new StoreQuoteLine(line.VariantId, line.Quantity)).ToList(),
                draft.Notes,
                draft.TakerUserId),
            cancellationToken);
        return ToCreated(result);
    }

    private static StoreOrderCreated ToCreated(StoreOrderResult result) =>
        new(result.OrderId, result.OrderNumber, result.Total, result.ClientId, result.AdvisorMembershipId, result.AlreadyExisted);

    private static StoreOrderParty ToParty(StoreOrderPartyInput party) =>
        new(
            party.Name, party.Phone, party.Email, party.Address, party.Country, party.DepartmentId, party.CityId, party.CityName,
            party.IdentificationType, party.IdentificationNumber);
}
