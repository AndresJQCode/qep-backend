using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Lo que dejó <see cref="ShopifyProductPricingResolver"/>: la tienda, su información en Shopify y la regla de precios, o
/// <see cref="FailureCode"/> (<c>company_required</c>, <c>currency_unsupported</c>) si no se puede escribir nada.</summary>
public sealed record ShopifyPricingResolution(
    ShopifyStore? Store, ShopifyShopInfo? Shop, ShopifyPricingContext? Pricing, string? FailureCode);

/// <summary>
/// Spec 2026-11-17 §5.3 y §10 (P2, P4), plan D4/D5: la regla de precios de una tienda, la misma para la importación y los webhooks.
/// El orden importa: primero la empresa de la tienda (sin llamar a Shopify), después la moneda de la tienda, y nada se crea si alguna
/// falta. Una falla de Shopify al leer la tienda sale como <see cref="ShopifyProductsSourceException"/>.
/// </summary>
public sealed class ShopifyProductPricingResolver(
    IShopifyStoreRepository stores,
    IShopifyBillingAccounts billing,
    IShopifyProductsSource source,
    IShopifyCatalogWriter catalog,
    ITenantPriceRounding rounding)
{
    public async Task<ShopifyPricingResolution> ResolveAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // P4: la empresa pudo quitarse después del POST; sin ella no se sabe si sumar el IVA.
        var store = await stores.FindAsync(connection.TenantId, connection.ConnectionId, cancellationToken);
        if (store?.Billing is not { } account)
        {
            return new ShopifyPricingResolution(store, null, null, ShopifyProductCodes.CompanyRequired);
        }

        var company = await billing.FindCompanyAsync(connection.TenantId, account.CompanyId, cancellationToken);
        if (company is null)
        {
            return new ShopifyPricingResolution(store, null, null, ShopifyProductCodes.CompanyRequired);
        }

        // P2: un solo precio, en la moneda de la tienda; fuera del catálogo de QEP no se crea nada.
        var shop = await source.ReadShopAsync(connection, cancellationToken);
        if (!Currencies.IsSupported(shop.CurrencyCode))
        {
            return new ShopifyPricingResolution(store, shop, null, ShopifyProductCodes.CurrencyUnsupported);
        }

        var currency = Currencies.Normalize(shop.CurrencyCode);
        var rule = await rounding.GetAsync(connection.TenantId, currency, cancellationToken);
        var rates = await catalog.FindDefaultTaxRatesAsync(connection.TenantId, cancellationToken);
        return new ShopifyPricingResolution(
            store, shop, new ShopifyPricingContext(currency, shop.TaxesIncluded, company.ChargesVat, rule, rates), null);
    }
}
