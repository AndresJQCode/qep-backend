using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 §5.4: Shopify escribe productos en Catalog por <see cref="IExternalProductWriter"/>. Vive en el composition root
/// porque Shopify no referencia Catalog (<c>ShopifyLayerTests</c>).
///
/// **Corre dentro de la transacción del <see cref="ShopifyDbContext"/> del scope** (sin ella lanza
/// <see cref="InvalidOperationException"/>). Cada llamada abre un scope hijo y enrola su <see cref="CatalogDbContext"/> en la conexión
/// y la transacción de Shopify, como <see cref="ShopifyOrderCorrectionWriter"/>: el producto, sus opciones y los vínculos que guarda
/// Shopify después se confirman juntos o no se confirman. El scope hijo se dispone al volver, así que nadie más usa ese
/// <see cref="CatalogDbContext"/> enrolado; un scope por llamada también deja limpio el rastreo de Catalog después de un rechazo.
///
/// Una <see cref="CatalogDomainException"/> vuelve como <see cref="ShopifyCatalogWrite.RejectedCode"/> (P7): si vino de un índice,
/// Postgres ya abortó la transacción, y quien llama la descarta. El resto se propaga.
/// </summary>
internal sealed class ShopifyCatalogWriter(
    ShopifyDbContext shopify,
    IServiceScopeFactory scopes,
    IClock clock) : IShopifyCatalogWriter
{
    public async Task<ShopifyCatalogWrite> CreateAsync(ShopifyCatalogProduct product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        try
        {
            var result = await InTransactionAsync(writer => writer.CreateAsync(Draft(product), cancellationToken), cancellationToken);
            return ToShopify(result);
        }
        catch (CatalogDomainException exception)
        {
            return ShopifyCatalogWrite.Rejected(exception.Code);
        }
    }

    public async Task<ShopifyCatalogWrite> UpdateAsync(
        Guid productId, ShopifyCatalogProduct product, IReadOnlyDictionary<long, Guid> existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(existing);
        var byKey = existing.ToDictionary(pair => KeyOf(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        try
        {
            var result = await InTransactionAsync(
                writer => writer.UpdateAsync(productId, Draft(product), byKey, cancellationToken), cancellationToken);
            return ToShopify(result);
        }
        catch (CatalogDomainException exception)
        {
            return ShopifyCatalogWrite.Rejected(exception.Code);
        }
    }

    public Task SetActiveAsync(Guid tenantId, Guid productId, bool active, Guid actorId, CancellationToken cancellationToken) =>
        InTransactionAsync(
            writer => writer.SetActiveAsync(tenantId, productId, active, actorId, clock.UtcNow, cancellationToken), cancellationToken);

    public Task ReleaseAsync(Guid tenantId, Guid productId, Guid actorId, CancellationToken cancellationToken) =>
        InTransactionAsync(
            async writer =>
            {
                await writer.ReleaseAsync(tenantId, productId, actorId, clock.UtcNow, cancellationToken);
                return true;
            },
            cancellationToken);

    public async Task<ShopifyDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rates = await scope.ServiceProvider.GetRequiredService<IExternalProductWriter>().FindDefaultTaxRatesAsync(tenantId, cancellationToken);
        return new ShopifyDefaultTaxRates(rates.Vat19Id, rates.ExemptId);
    }

    private async Task<T> InTransactionAsync<T>(Func<IExternalProductWriter, Task<T>> write, CancellationToken cancellationToken)
    {
        var transaction = shopify.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A Shopify catalog write must run inside the Shopify transaction.");

        await using var scope = scopes.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalog.Database.SetDbConnection(shopify.Database.GetDbConnection(), contextOwnsConnection: false);
        await catalog.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        return await write(scope.ServiceProvider.GetRequiredService<IExternalProductWriter>());
    }

    private static ExternalProductDraft Draft(ShopifyCatalogProduct product) =>
        new(
            product.TenantId,
            Product.ShopifySource,
            product.Name,
            product.IsActive,
            product.TaxRateId,
            product.Currency,
            [.. product.Options.Select(option => new ExternalOptionDraft(option.Name, option.Values))],
            [.. product.Variants.Select(variant => new ExternalVariantDraft(
                KeyOf(variant.ShopifyVariantId), variant.Code, variant.Barcode, variant.OptionValues, variant.Price))],
            product.ActorId,
            product.OccurredAt);

    private static ShopifyCatalogWrite ToShopify(ExternalProductResult result) =>
        new(
            result.ProductId,
            result.VariantIdsByKey.ToDictionary(pair => ParseKey(pair.Key), pair => pair.Value),
            [.. result.DroppedBarcodeKeys.Select(ParseKey)],
            result.Changed,
            null);

    private static string KeyOf(long shopifyVariantId) => shopifyVariantId.ToString(CultureInfo.InvariantCulture);

    private static long ParseKey(string key) => long.Parse(key, NumberStyles.None, CultureInfo.InvariantCulture);
}
