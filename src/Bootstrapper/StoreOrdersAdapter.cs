using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
internal sealed partial class StoreOrdersAdapter(IServiceScopeFactory scopes, ILogger<StoreOrdersAdapter> logger) : IStoreOrders
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
        catch (QuotationsDomainException exception)
        {
            // Al visitante le llega «línea no disponible», pero una configuración rota del motor (tasa de IVA, redondeo) se vería igual:
            // el código original queda en el log y la excepción, entera, como interna. Nunca el carrito ni datos del visitante.
            LogQuoteRejected(logger, tenantId, currency, exception.Code);
            throw new StoreDomainException(
                StoreErrorCodes.LineUnavailable, "A line of the cart cannot be sold.", exception);
        }
    }

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "The quotation engine rejected a public store quote for tenant {TenantId} in {Currency} with code {Code}")]
    private static partial void LogQuoteRejected(ILogger logger, Guid tenantId, string currency, string code);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "The quotation engine rejected a public store order for tenant {TenantId} in {Currency} with code {Code}")]
    private static partial void LogCreateRejected(ILogger logger, Guid tenantId, string currency, string code);

    public async Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        return await gateway.FindExistingAsync(tenantId, clientRequestId, cancellationToken) is { } existing ? ToCreated(existing) : null;
    }

    public async Task<int> CountCreatedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        return await gateway.CountStoreOrdersSinceAsync(tenantId, since, cancellationToken);
    }

    // Como en la cotización, el pedido lo crea el checkout del visitante anónimo: un rechazo del motor sale como línea no
    // disponible, con el código original en el log y la excepción como interna.
    public async Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var scope = scopes.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStoreOrderGateway>();
        try
        {
            var result = await gateway.CreateAsync(ToGatewayDraft(draft), cancellationToken);
            return ToCreated(result);
        }
        catch (QuotationsDomainException exception)
        {
            LogCreateRejected(logger, draft.TenantId, draft.BillingAccount.Currency, exception.Code);
            throw new StoreDomainException(
                StoreErrorCodes.LineUnavailable, "A line of the order cannot be sold.", exception);
        }
    }

    private static Modules.Quotations.Application.StoreOrderDraft ToGatewayDraft(StoreOrderDraft draft) =>
        new(
            draft.TenantId,
            draft.ClientRequestId,
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
            draft.Notes);

    private static StoreOrderCreated ToCreated(StoreOrderResult result) =>
        new(
            result.OrderId, result.OrderNumber, result.Total, result.Currency, result.ClientId, result.AdvisorMembershipId,
            result.AlreadyExisted);

    private static StoreOrderParty ToParty(StoreOrderPartyInput party) =>
        new(
            party.Name, party.Phone, party.Email, party.Address, party.Country, party.DepartmentId, party.CityId, party.CityName,
            party.IdentificationType, party.IdentificationNumber);
}
