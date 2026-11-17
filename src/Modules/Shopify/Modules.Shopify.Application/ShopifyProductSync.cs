using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Quién aplica el producto: la importación (audita como quien la pidió) o un webhook (audita como sistema, P3).</summary>
public enum ShopifySyncTrigger
{
    Import,
    Webhook,
}

/// <summary>La tienda, su regla de precios y el actor con que firma Catalog (<see cref="Guid.Empty"/> es el sistema, P3).</summary>
public sealed record ShopifyProductSyncContext(
    ShopifyConnectionRef Connection, ShopifyPricingContext Pricing, Guid ActorId, ShopifySyncTrigger Trigger);

public enum ShopifyProductOutcomeKind
{
    /// <summary>No había nada de QEP: se creó el producto y sus vínculos <c>Imported</c>.</summary>
    Created,

    /// <summary>Producto importado: se aplicó el snapshot.</summary>
    Updated,

    /// <summary>Se vincularon variantes que ya existían (<c>MatchedBySku</c>).</summary>
    Matched,

    /// <summary>P9: un producto importado que Shopify borró y recreó volvió a vincularse y se reactivó.</summary>
    Relinked,

    /// <summary>Nada que escribir: un snapshot más viejo que el aplicado, o uno sin cambios.</summary>
    Unchanged,

    /// <summary>El producto importado quedó inactivo (archivado o borrado en Shopify).</summary>
    Deactivated,

    /// <summary>Catalog lo rechazó (P7) o perdió dos veces la carrera: no se escribió nada.</summary>
    Skipped,

    /// <summary>No es de QEP y no hay nada que hacer (archivado sin vínculos, o borrado de algo no importado).</summary>
    Ignored,
}

/// <summary>Cómo terminó un producto. <see cref="Tally"/> cuenta como la importación (plan D3); <see cref="IssueCodes"/> son los
/// avisos que la importación anota (o el webhook loguea).</summary>
public sealed record ShopifyProductOutcome(ShopifyProductOutcomeKind Kind, ShopifyProductTally Tally, IReadOnlyList<string> IssueCodes);

/// <summary>
/// Spec 2026-11-17 §5.3, §6 y §10: aplica un producto de Shopify a Catalog, el mismo camino para la importación y los webhooks.
/// <para><b>Una transacción por producto</b>, con el candado <c>(20261118, hashtext('{conexión}:{producto}'))</c>: el webhook y la
/// importación del mismo producto se serializan, y el segundo ve los vínculos del primero (Review Focus 1). Dentro: los vínculos, el
/// producto (por <see cref="IShopifyCatalogWriter"/>, enrolado en la misma transacción) y los vínculos nuevos. Un rechazo de Catalog
/// deshace todo (P7: nunca a medias) y vuelve <see cref="ShopifyProductOutcomeKind.Skipped"/> con el código; si el rechazo fue
/// <c>catalog.option.name_taken</c> (otro producto creó la misma opción a la vez) o un <c>concurrency.conflict</c> de los vínculos, se
/// reintenta una vez y después es <c>concurrent_write</c>.</para>
/// <para><b>Vínculos colgando</b> (Review Focus 4): un vínculo cuya variante de QEP ya no existe (producto borrado a mano) se borra y
/// el producto se trata como si no tuviera vínculos.</para>
/// <para><b>P11, la tienda vuelta a enlazar.</b> Borrar la conexión y enlazar la tienda otra vez da otra conexión, y los vínculos
/// quedan en la vieja. Las conexiones viejas son las otras tiendas del mismo tenant con el mismo dominio
/// (<see cref="IShopifyStoreRepository.FindByShopDomainAsync"/>, como <c>AdoptWaitingOrdersAsync</c>). Un producto de Shopify sin
/// vínculos en la conexión actual adopta los suyos de una vieja (se mueven, con su origen, versión e imagen): casa por id de
/// Shopify, así que también un producto sin SKU. Y el SKU que casa con un importado de una vieja cuenta para P9. Los vínculos de la
/// vieja no se sueltan al desinstalar ni con <c>shop/redact</c> (no son datos personales): son lo que permite adoptarlos.</para>
/// <para><b>P9 sin robos.</b> El SKU que casa con un producto importado sólo lo vuelve a vincular si el producto de Shopify de sus
/// vínculos ya no existe (<see cref="IShopifyProductsSource.ReadProductAsync"/> da <c>null</c>). Si sigue vivo, el nuevo es un
/// duplicado: se salta con <c>sku_in_other_product</c>, sin escribir nada, o los dos se quitarían los vínculos en cada webhook.</para>
/// <para><b>La imagen</b> va después del commit, fuera de la transacción: sólo en un producto importado, y sólo si el id de la imagen
/// destacada cambió (o es nuevo). Si falla, el producto queda y se anota <c>image_failed</c>.</para>
/// </summary>
public sealed class ShopifyProductSync(
    IShopifyUnitOfWork unitOfWork,
    IShopifyProductLinkRepository links,
    IShopifyCatalogWriter catalog,
    IShopifyProductLookup products,
    IShopifyImageFetcher images,
    IShopifyProductImageStore imageStore,
    IShopifyProductsSource source,
    IShopifyStoreRepository stores,
    IClock clock)
{
    public const string OptionNameTaken = "catalog.option.name_taken";

    private const string ConcurrencyConflict = "concurrency.conflict";

    private const int MaxAttempts = 2;

    public async Task<ShopifyProductOutcome> ApplyAsync(
        ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(product);

        for (var attempt = 1; ; attempt++)
        {
            Applied applied;
            try
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
                await unitOfWork.LockProductAsync(context.Connection.ConnectionId, product.Id, cancellationToken);
                applied = await ApplyLockedAsync(context, product, cancellationToken);
                if (applied.RejectedCode is null)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch (RequestConcurrencyException exception) when (exception.Code == ConcurrencyConflict)
            {
                // Otro escritor vinculó lo mismo a la vez: lo pendiente se descarta y se relee.
                unitOfWork.DiscardPendingChanges();
                if (attempt < MaxAttempts)
                {
                    continue;
                }

                return Skipped(product, ShopifyProductCodes.ConcurrentWrite);
            }

            if (applied.RejectedCode is { } code)
            {
                // La transacción ya se dispuso sin commit (rollback): lo que Catalog o los vínculos dejaron pendiente se olvida.
                unitOfWork.DiscardPendingChanges();
                if (code == OptionNameTaken && attempt < MaxAttempts)
                {
                    continue;
                }

                return Skipped(product, code == OptionNameTaken ? ShopifyProductCodes.ConcurrentWrite : code);
            }

            if (applied.Image is { } image)
            {
                if (await AttachImageAsync(context, product, image, cancellationToken) is { } imageIssue)
                {
                    return applied.Outcome with { IssueCodes = [.. applied.Outcome.IssueCodes, imageIssue] };
                }
            }

            return applied.Outcome;
        }
    }

    /// <summary>§6, <c>products/delete</c>: desactiva el producto importado y deja los vínculos (nunca borra: las líneas de los
    /// documentos lo referencian). Uno vinculado por SKU, o sin vínculos, no se toca.</summary>
    public async Task<ShopifyProductOutcome> DeleteAsync(
        ShopifyProductSyncContext context, long shopifyProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Connection;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockProductAsync(connection.ConnectionId, shopifyProductId, cancellationToken);
        var current = await links.FindByShopifyProductAsync(connection.ConnectionId, shopifyProductId, cancellationToken);
        var imported = current.FirstOrDefault(link => link.Origin == ShopifyProductLinkOrigin.Imported);
        if (imported is null
            || (await products.FindByVariantIdsAsync(connection.TenantId, [.. current.Select(link => link.VariantId)], cancellationToken)).Count == 0)
        {
            return new ShopifyProductOutcome(ShopifyProductOutcomeKind.Ignored, OneProduct, []);
        }

        await catalog.SetActiveAsync(connection.TenantId, imported.ProductId, false, context.ActorId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ShopifyProductOutcome(ShopifyProductOutcomeKind.Deactivated, OneProduct, []);
    }

    private static ShopifyProductTally OneProduct => new(1, 0, 0, 0, 0);

    private static ShopifyProductOutcome Skipped(ShopifyProductSnapshot product, string code) =>
        new(ShopifyProductOutcomeKind.Skipped, new ShopifyProductTally(1, 0, 0, 0, product.Variants.Count), [code]);

    private async Task<Applied> ApplyLockedAsync(
        ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken)
    {
        var connection = context.Connection;
        var oldConnections = await OldConnectionsAsync(connection, cancellationToken);
        var current = (await links.FindByShopifyProductAsync(connection.ConnectionId, product.Id, cancellationToken)).ToList();
        if (current.Count == 0 && oldConnections.Count > 0)
        {
            current = await AdoptAsync(connection, oldConnections, product.Id, cancellationToken);
        }

        if (current.Count > 0)
        {
            // Review Focus 4: los vínculos cuya variante de QEP ya no existe se borran; si no queda ninguno, es un producto nuevo.
            var alive = await products.FindByVariantIdsAsync(
                connection.TenantId, [.. current.Select(link => link.VariantId)], cancellationToken);
            var stale = current.Where(link => !alive.ContainsKey(link.VariantId)).ToArray();
            foreach (var link in stale)
            {
                links.Remove(link);
                current.Remove(link);
            }

            if (stale.Length > 0)
            {
                // Se guarda antes de vincular de nuevo: el vínculo nuevo de la misma variante de Shopify chocaría con el viejo en
                // el índice único si los dos fueran en el mismo guardado.
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            if (current.Count > 0 && current.Max(link => link.ShopifyUpdatedAt) is { } applied && applied >= product.UpdatedAt)
            {
                // §6, orden: un snapshot que no es más nuevo que el aplicado no pisa (vale también para la importación).
                return new Applied(new ShopifyProductOutcome(ShopifyProductOutcomeKind.Unchanged, OneProduct, []), null, null);
            }
        }

        if (current.Any(link => link.Origin == ShopifyProductLinkOrigin.Imported))
        {
            return await UpdateImportedAsync(context, product, current, current[0].ProductId, ShopifyProductOutcomeKind.Updated, 0, cancellationToken);
        }

        if (current.Count > 0)
        {
            return await LinkNewVariantsAsync(context, product, current, cancellationToken);
        }

        if (product.Status == ShopifyProductStatus.Archived)
        {
            return new Applied(new ShopifyProductOutcome(ShopifyProductOutcomeKind.Ignored, OneProduct, []), null, null);
        }

        var skus = ShopifyProductMatcher.SkusOf(product);
        var byCode = skus.Count == 0
            ? new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal)
            : await products.FindByCodesAsync(connection.TenantId, [.. skus], cancellationToken);
        Guid[] scope = [connection.ConnectionId, .. oldConnections];
        var imported = byCode.Count == 0 ? new HashSet<Guid>() : await links.ListImportedProductIdsAsync(scope, cancellationToken);
        var match = ShopifyProductMatcher.Match(product, byCode, imported);
        var now = clock.UtcNow;
        switch (match.Kind)
        {
            case ShopifyMatchKind.None:
                return await CreateAsync(context, product, cancellationToken);
            case ShopifyMatchKind.Relink:
                var old = await links.FindByProductAsync(connection.TenantId, scope, match.QepProductId!.Value, cancellationToken);
                return await AnyStillInShopifyAsync(connection, old, product.Id, cancellationToken)
                    ? Rejected(ShopifyProductCodes.SkuInOtherProduct)
                    : await RelinkAsync(context, product, match, old, cancellationToken);
            default:
                return LinkMatched(context, product, match, now);
        }
    }

    // P11: las otras conexiones del tenant con la tienda del mismo dominio (las que quedaron al volver a enlazarla).
    private async Task<IReadOnlyList<Guid>> OldConnectionsAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        var domain = connection.ShopDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain))
        {
            return [];
        }

        return [.. (await stores.FindByShopDomainAsync(domain, cancellationToken))
            .Where(store => store.TenantId == connection.TenantId && store.ConnectionId != connection.ConnectionId)
            .Select(store => store.ConnectionId)
            .Distinct()];
    }

    // P11: los vínculos del mismo producto de Shopify en una conexión vieja pasan a la actual, con su origen, versión e imagen. Se
    // guarda el borrado antes de agregar, como en RelinkAsync.
    private async Task<List<ShopifyProductLink>> AdoptAsync(
        ShopifyConnectionRef connection, IReadOnlyList<Guid> oldConnections, long shopifyProductId, CancellationToken cancellationToken)
    {
        var orphaned = await links.FindByShopifyProductInAsync(oldConnections, shopifyProductId, cancellationToken);
        if (orphaned.Count == 0)
        {
            return [];
        }

        foreach (var link in orphaned)
        {
            links.Remove(link);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var now = clock.UtcNow;
        var adopted = new List<ShopifyProductLink>();
        foreach (var old in orphaned.GroupBy(link => link.ShopifyVariantId).Select(group => group.First()))
        {
            var link = ShopifyProductLink.Create(
                connection.TenantId, connection.ConnectionId, old.ShopifyProductId, old.ShopifyVariantId, old.ProductId, old.VariantId,
                old.Origin, old.ShopifyUpdatedAt, now);
            link.RecordImage(old.ShopifyImageId, old.ImageFileId, now);
            links.Add(link);
            adopted.Add(link);
        }

        return adopted;
    }

    // P9: un producto de Shopify de los vínculos viejos que sigue existiendo dice que el nuevo es un duplicado, no un recreado.
    private async Task<bool> AnyStillInShopifyAsync(
        ShopifyConnectionRef connection, IReadOnlyList<ShopifyProductLink> old, long current, CancellationToken cancellationToken)
    {
        foreach (var shopifyProductId in old.Select(link => link.ShopifyProductId).Where(id => id != current).Distinct())
        {
            if (await source.ReadProductAsync(connection, shopifyProductId, cancellationToken) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Applied> CreateAsync(ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken)
    {
        var connection = context.Connection;
        var now = clock.UtcNow;
        var mapped = ShopifyProductMapper.Map(product, context.Pricing, connection.TenantId, context.ActorId, now);
        var write = await catalog.CreateAsync(mapped.Product, cancellationToken);
        if (write.RejectedCode is { } code)
        {
            return Rejected(code);
        }

        var created = AddLinks(connection, product, write.ProductId, write.VariantIds, ShopifyProductLinkOrigin.Imported, now);
        var issues = mapped.IssueCodes.ToList();
        if (write.DroppedBarcodes.Count > 0)
        {
            issues.Add(ShopifyProductCodes.BarcodeTaken);
        }

        return new Applied(
            new ShopifyProductOutcome(ShopifyProductOutcomeKind.Created, new ShopifyProductTally(1, 1, 0, mapped.WithoutSku, 0), issues),
            null,
            new PendingImage(write.ProductId, created, null, null));
    }

    // P9: el SKU casó con un producto importado de esta conexión (o de una vieja de la misma tienda, P11) que Shopify borró y
    // recreó con otro id. Los vínculos viejos de ese producto se borran (y se guardan antes de agregar los nuevos: el índice único
    // por variante de QEP no deja convivir los dos), se reactiva y sigue como un importado.
    private async Task<Applied> RelinkAsync(
        ShopifyProductSyncContext context,
        ShopifyProductSnapshot product,
        ShopifyMatch match,
        IReadOnlyList<ShopifyProductLink> old,
        CancellationToken cancellationToken)
    {
        var connection = context.Connection;
        var productId = match.QepProductId!.Value;
        var previousImage = old.FirstOrDefault(link => link.ImageFileId is not null);
        foreach (var link in old)
        {
            links.Remove(link);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await catalog.SetActiveAsync(connection.TenantId, productId, true, context.ActorId, cancellationToken);

        var now = clock.UtcNow;
        var relinked = AddLinks(connection, product, productId, match.Matched, ShopifyProductLinkOrigin.Imported, now);
        foreach (var link in relinked)
        {
            link.RecordImage(previousImage?.ShopifyImageId, previousImage?.ImageFileId, now);
        }

        return await UpdateImportedAsync(
            context, product, relinked, productId, ShopifyProductOutcomeKind.Relinked, match.Matched.Count, cancellationToken);
    }

    // §6, update de un importado: el snapshot entero (nombre, opciones, variantes, códigos, precios y estado). Las variantes nuevas
    // reciben vínculo; las que ya no están pierden el suyo (Catalog las borra o las desactiva según su uso).
    private async Task<Applied> UpdateImportedAsync(
        ShopifyProductSyncContext context,
        ShopifyProductSnapshot product,
        IReadOnlyList<ShopifyProductLink> current,
        Guid productId,
        ShopifyProductOutcomeKind kind,
        int matched,
        CancellationToken cancellationToken)
    {
        var connection = context.Connection;
        var now = clock.UtcNow;
        var mapped = ShopifyProductMapper.Map(product, context.Pricing, connection.TenantId, context.ActorId, now);
        var existing = current
            .Where(link => product.Variants.Any(variant => variant.Id == link.ShopifyVariantId))
            .ToDictionary(link => link.ShopifyVariantId, link => link.VariantId);
        var write = await catalog.UpdateAsync(productId, mapped.Product, existing, cancellationToken);
        if (write.RejectedCode is { } code)
        {
            return Rejected(code);
        }

        var kept = new List<ShopifyProductLink>();
        foreach (var link in current)
        {
            if (write.VariantIds.ContainsKey(link.ShopifyVariantId))
            {
                link.RecordSync(product.UpdatedAt, now);
                kept.Add(link);
            }
            else
            {
                links.Remove(link);
            }
        }

        var added = AddLinks(
            connection,
            product,
            productId,
            write.VariantIds.Where(pair => kept.All(link => link.ShopifyVariantId != pair.Key)).ToDictionary(),
            ShopifyProductLinkOrigin.Imported,
            now);
        var all = kept.Concat(added).ToList();
        foreach (var link in added)
        {
            link.RecordImage(kept.FirstOrDefault()?.ShopifyImageId, kept.FirstOrDefault()?.ImageFileId, now);
        }

        var issues = new List<string>();
        if (write.DroppedBarcodes.Count > 0)
        {
            issues.Add(ShopifyProductCodes.BarcodeTaken);
        }

        var outcomeKind = kind == ShopifyProductOutcomeKind.Relinked
            ? kind
            : !write.Changed ? ShopifyProductOutcomeKind.Unchanged
            : product.Status == ShopifyProductStatus.Archived ? ShopifyProductOutcomeKind.Deactivated : kind;
        var withoutSku = kind == ShopifyProductOutcomeKind.Relinked ? mapped.WithoutSku : 0;
        var reference = all.FirstOrDefault();
        return new Applied(
            new ShopifyProductOutcome(outcomeKind, new ShopifyProductTally(1, 0, matched, withoutSku, 0), issues),
            null,
            new PendingImage(productId, all, reference?.ShopifyImageId, reference?.ImageFileId));
    }

    // S-2: con vínculos MatchedBySku el producto de QEP nunca se escribe; sólo las variantes nuevas cuyo SKU casa en ese mismo
    // producto se vinculan (P6: una de otro producto se salta).
    private async Task<Applied> LinkNewVariantsAsync(
        ShopifyProductSyncContext context, ShopifyProductSnapshot product, List<ShopifyProductLink> current, CancellationToken cancellationToken)
    {
        var connection = context.Connection;
        var now = clock.UtcNow;
        var qepProductId = current[0].ProductId;
        foreach (var gone in current.Where(link => product.Variants.All(variant => variant.Id != link.ShopifyVariantId)).ToArray())
        {
            links.Remove(gone);
            current.Remove(gone);
        }

        var fresh = product.Variants.Where(variant => current.All(link => link.ShopifyVariantId != variant.Id)).ToArray();
        var skus = fresh.Select(variant => variant.Sku?.Trim()).Where(sku => !string.IsNullOrEmpty(sku)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        var byCode = skus.Length == 0
            ? new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal)
            : await products.FindByCodesAsync(connection.TenantId, skus, cancellationToken);

        var linkedVariants = current.Select(link => link.VariantId).ToHashSet();
        var newLinks = new Dictionary<long, Guid>();
        var inOther = 0;
        var unmatched = 0;
        foreach (var variant in fresh)
        {
            var sku = variant.Sku?.Trim();
            if (string.IsNullOrEmpty(sku) || !byCode.TryGetValue(sku, out var found))
            {
                unmatched++;
            }
            else if (found.ProductId != qepProductId)
            {
                inOther++;
            }
            else if (linkedVariants.Add(found.VariantId))
            {
                newLinks[variant.Id] = found.VariantId;
            }
            else
            {
                unmatched++;
            }
        }

        foreach (var link in current)
        {
            link.RecordSync(product.UpdatedAt, now);
        }

        AddLinks(connection, product, qepProductId, newLinks, ShopifyProductLinkOrigin.MatchedBySku, now);
        var issues = new List<string>();
        if (unmatched > 0)
        {
            issues.Add(ShopifyProductCodes.PartialMatch);
        }

        if (inOther > 0)
        {
            issues.Add(ShopifyProductCodes.SkuInOtherProduct);
        }

        return new Applied(
            new ShopifyProductOutcome(
                newLinks.Count > 0 ? ShopifyProductOutcomeKind.Matched : ShopifyProductOutcomeKind.Unchanged,
                new ShopifyProductTally(1, 0, newLinks.Count, 0, unmatched + inOther),
                issues),
            null,
            null);
    }

    // §5.3: todas o algunas variantes casaron por SKU en un producto que no es importado: se vinculan, no se crea nada.
    private Applied LinkMatched(ShopifyProductSyncContext context, ShopifyProductSnapshot product, ShopifyMatch match, DateTimeOffset now)
    {
        AddLinks(context.Connection, product, match.QepProductId!.Value, match.Matched, ShopifyProductLinkOrigin.MatchedBySku, now);
        var issues = new List<string>();
        if (match.PartialMatch.Count > 0)
        {
            issues.Add(ShopifyProductCodes.PartialMatch);
        }

        if (match.InOtherProduct.Count > 0)
        {
            issues.Add(ShopifyProductCodes.SkuInOtherProduct);
        }

        return new Applied(
            new ShopifyProductOutcome(
                ShopifyProductOutcomeKind.Matched,
                new ShopifyProductTally(1, 0, match.Matched.Count, 0, match.PartialMatch.Count + match.InOtherProduct.Count),
                issues),
            null,
            null);
    }

    private List<ShopifyProductLink> AddLinks(
        ShopifyConnectionRef connection,
        ShopifyProductSnapshot product,
        Guid productId,
        IReadOnlyDictionary<long, Guid> variantIds,
        ShopifyProductLinkOrigin origin,
        DateTimeOffset now)
    {
        var added = new List<ShopifyProductLink>();
        // En el orden de Shopify, para que los vínculos salgan en un orden estable.
        foreach (var variant in product.Variants)
        {
            if (variantIds.TryGetValue(variant.Id, out var variantId))
            {
                var link = ShopifyProductLink.Create(
                    connection.TenantId, connection.ConnectionId, product.Id, variant.Id, productId, variantId, origin,
                    product.UpdatedAt, now);
                links.Add(link);
                added.Add(link);
            }
        }

        return added;
    }

    // Fuera de la transacción del producto (P1). Sólo si la imagen destacada es otra que la guardada (o el producto es nuevo).
    private async Task<string?> AttachImageAsync(
        ShopifyProductSyncContext context, ShopifyProductSnapshot product, PendingImage pending, CancellationToken cancellationToken)
    {
        if (product.FeaturedImage is not { } featured || featured.Id == pending.StoredImageId || pending.Links.Count == 0)
        {
            return null;
        }

        try
        {
            var fetched = await images.FetchAsync(featured.Url, cancellationToken);
            if (fetched is null)
            {
                return ShopifyProductCodes.ImageFailed;
            }

            var attached = await imageStore.AttachAsync(
                context.Connection.TenantId, pending.ProductId, fetched, pending.StoredFileId, context.ActorId, cancellationToken);
            if (attached.FailureCode is { } failure)
            {
                return failure;
            }

            var now = clock.UtcNow;
            foreach (var link in pending.Links)
            {
                link.RecordImage(featured.Id, attached.FileId, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // El producto ya está confirmado: una imagen que no entra nunca lo deshace (§5.3).
            unitOfWork.DiscardPendingChanges();
            return ShopifyProductCodes.ImageFailed;
        }
    }

    private static Applied Rejected(string code) => new(null!, code, null);

    /// <summary>Lo que dejó la transacción: el resultado, el código si Catalog la rechazó, y la imagen pendiente de un importado.</summary>
    private sealed record Applied(ShopifyProductOutcome Outcome, string? RejectedCode, PendingImage? Image);

    private sealed record PendingImage(Guid ProductId, IReadOnlyList<ShopifyProductLink> Links, long? StoredImageId, Guid? StoredFileId);
}
