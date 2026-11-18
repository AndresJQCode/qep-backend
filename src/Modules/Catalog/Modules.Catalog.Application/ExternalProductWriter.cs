using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Ver <see cref="IExternalProductWriter"/>. Las reglas son las del POST y el PUT, por las mismas
/// piezas (<see cref="ProductVariantReferences"/>, <see cref="ProductVariantChanges"/>,
/// <see cref="ProductTaxRateResolver"/>, <see cref="ProductPriceChangeDetector"/>); lo propio de acá es
/// lo que un formulario no tiene: opciones por nombre, códigos de barras que se omiten en vez de
/// rechazar el producto, y un precio en una sola moneda que no pisa lo que el asesor agregó en QEP.
/// </summary>
public sealed class ExternalProductWriter(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IOptionDefinitionRepository optionRepository,
    IVariantUsageChecker usageChecker,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IProductImageReleasePublisher imageReleasePublisher) : IExternalProductWriter
{
    public async Task<ExternalProductResult> CreateAsync(
        ExternalProductDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var currency = Currencies.Normalize(draft.Currency);
        var taxRateId = await ProductTaxRateResolver.ResolveAsync(
            taxRateRepository, draft.TenantId, draft.TaxRateId, cancellationToken);

        var resolved = await ResolveOptionsAsync(draft, cancellationToken);
        var (barcodes, dropped) = await KeepAvailableBarcodesAsync(draft, product: null, cancellationToken);

        var variants = draft.Variants
            .Select((variant, index) => new ProductVariantInput
            {
                Code = variant.Code,
                Barcode = barcodes[index],
                OptionValueIds = resolved.ValueIds[index],
                Pricing = new ProductPricing
                {
                    Prices = variant.Price is { } price
                        ? new Dictionary<string, decimal>(StringComparer.Ordinal) { [currency] = price }
                        : new Dictionary<string, decimal>(StringComparer.Ordinal),
                    AllowsNoPrice = variant.Price is null,
                },
                IsActive = true,
                // ProductVariantCost exige un monto mayor que cero: sin eso, la variante nace sin costo (D-18).
                Costs = variant.Cost is > 0m and var cost
                    ? new Dictionary<string, decimal>(StringComparer.Ordinal) { [currency] = cost }
                    : null,
            })
            .ToArray();

        ProductVariantReferences.EnsureValid(null, resolved.OptionIds, variants, resolved.Library);
        await ProductVariantReferences.EnsureCodesAvailableAsync(
            repository, draft.TenantId, null, variants, cancellationToken);

        // Con el Create de opciones también sin opciones: es el que guarda el origen. Con una lista
        // vacía es el mismo producto de una variante por defecto que arma el otro Create.
        var product = Product.Create(
            ProductId.New(),
            draft.TenantId,
            draft.Name,
            new ProductDetails { TaxRateId = taxRateId },
            resolved.OptionIds,
            variants,
            [],
            draft.OccurredAt,
            externalSource: draft.Source);
        if (!draft.IsActive)
        {
            product.Deactivate(draft.OccurredAt);
        }

        repository.Add(product);
        auditPublisher.Publish(
            draft.TenantId, draft.ActorId, "catalog.product.created", product.Id.ToString(), "success", draft.OccurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExternalProductResult(
            product.Id.Value, product.Version, KeysOf(draft, product, targets: null), dropped, Changed: true);
    }

    public async Task<ExternalProductResult> UpdateAsync(
        Guid productId,
        ExternalProductDraft draft,
        IReadOnlyDictionary<string, Guid> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(existing);
        // Sólo el alta deja un producto sin precio (D-16): una plataforma que actualiza siempre manda el suyo.
        if (draft.Variants.Any(variant => variant.Price is null))
        {
            throw new ArgumentException("An update needs a price on every variant.", nameof(draft));
        }

        var product = await repository.FindAsync(draft.TenantId, new ProductId(productId), cancellationToken)
            ?? throw ProductNotFound.For(productId);
        var currency = Currencies.Normalize(draft.Currency);

        var savedById = product.Variants.ToDictionary(variant => variant.Id.Value);
        var targets = draft.Variants
            .Select(variant => existing.TryGetValue(variant.Key, out var id) && savedById.ContainsKey(id) ? id : (Guid?)null)
            .ToArray();
        var targeted = targets.OfType<Guid>().ToHashSet();
        var removed = product.Variants.Where(variant => !targeted.Contains(variant.Id.Value)).ToArray();
        // Una variante usada que una actualización anterior ya dejó inactiva sigue fuera del borrador para siempre: no es un
        // cambio, o cada products/update reescribiría el producto.
        var keptInactive = removed.Count(variant => !variant.IsActive);
        var (barcodes, dropped) = await KeepAvailableBarcodesAsync(draft, product, cancellationToken);

        // Review Focus 2: Shopify manda products/update también por inventario. Sin cambios no se toca
        // ni la biblioteca ni el producto: subir la versión le daría un 412 al asesor que lo tiene abierto.
        if (removed.Length == keptInactive
            && await IsUnchangedAsync(product, draft, currency, targets, barcodes, savedById, keptInactive, cancellationToken))
        {
            return new ExternalProductResult(
                product.Id.Value, product.Version, KeysOf(draft, product, targets), dropped, Changed: false);
        }

        var resolved = await ResolveOptionsAsync(draft, cancellationToken);
        // Las opciones de antes también: proyectar los valores de una variante que se queda inactiva
        // necesita saber qué opción era cada posición (como el PUT).
        var library = (await ProductOptionLibrary.LoadAsync(
                optionRepository, draft.TenantId, product.OptionIds, cancellationToken))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var (id, option) in resolved.Library)
        {
            library[id] = option;
        }

        // Una sola comprobación de uso, sin la segunda del PUT: este camino no abre transacción, la
        // abre quien llama, y la consulta corre dentro de ella.
        var removedIds = removed.Select(variant => variant.Id.Value).ToArray();
        var used = await usageChecker.FindUsedAsync(draft.TenantId, removedIds, cancellationToken);
        var deletedIds = removedIds.Where(id => !used.Contains(id)).ToHashSet();
        var kept = removed.Where(variant => used.Contains(variant.Id.Value)).ToArray();

        var requests = draft.Variants
            .Select((variant, index) => new ProductVariantRequest(
                targets[index],
                variant.Code,
                barcodes[index],
                resolved.ValueIds[index],
                // Una guardada hereda su precio entero (escalas, empaques, otras monedas) y abajo se le
                // cambia sólo la moneda de la tienda; una nueva nace con ese único precio.
                targets[index] is null
                    ? new ProductPricingRequest(
                        new Dictionary<string, decimal>(StringComparer.Ordinal) { [currency] = variant.Price!.Value }, null, null)
                    : null,
                GalleryFileOf(product, targets[index]),
                IsActive: true))
            .Concat(kept.Select(variant => new ProductVariantRequest(
                variant.Id.Value,
                variant.Code,
                variant.Barcode,
                OptionValueIds: null,
                Pricing: null,
                GalleryFileOf(product, variant.Id.Value),
                IsActive: false)))
            .ToArray();

        var variants = ProductVariantChanges.Resolve(product, resolved.OptionIds, requests, library)
            .Select((input, index) => index < draft.Variants.Count && targets[index] is not null
                ? input with { Pricing = input.Pricing with { Prices = WithPrice(input.Pricing.Prices, currency, draft.Variants[index].Price!.Value) } }
                : input)
            .ToArray();

        ProductVariantReferences.EnsureValid(product, resolved.OptionIds, variants, library);
        await ProductVariantReferences.EnsureCodesAvailableAsync(
            repository, draft.TenantId, product, variants, cancellationToken);

        repository.AddPriceChanges(ProductPriceChangeDetector.Detect(product, variants, draft.ActorId, draft.OccurredAt));

        // D8: Update exige activo. Un producto inactivo se activa, se actualiza y vuelve a quedar como
        // dice el borrador, en el mismo guardado.
        if (!product.IsActive)
        {
            product.Activate(draft.OccurredAt);
        }

        // La tasa, la descripción, la categoría, la galería y la visibilidad son del asesor (S-2).
        product.Update(
            draft.Name,
            new ProductDetails
            {
                Description = product.Description,
                TaxRateId = product.TaxRateId,
                CategoryId = product.CategoryId
            },
            resolved.OptionIds,
            variants,
            product.Gallery.Select(image => image.FileId).ToArray(),
            draft.OccurredAt,
            deletedIds,
            visibleInStore: null);
        if (!draft.IsActive)
        {
            product.Deactivate(draft.OccurredAt);
        }

        auditPublisher.Publish(
            draft.TenantId, draft.ActorId, "catalog.product.updated", product.Id.ToString(), "success", draft.OccurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExternalProductResult(
            product.Id.Value, product.Version, KeysOf(draft, product, targets), dropped, Changed: true);
    }

    public async Task<bool> SetActiveAsync(
        Guid tenantId,
        Guid productId,
        bool active,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(tenantId, new ProductId(productId), cancellationToken)
            ?? throw ProductNotFound.For(productId);
        if (product.IsActive == active)
        {
            return false;
        }

        if (active)
        {
            product.Activate(occurredAt);
        }
        else
        {
            product.Deactivate(occurredAt);
        }

        auditPublisher.Publish(
            tenantId,
            actorId,
            active ? "catalog.product.activated" : "catalog.product.deactivated",
            product.Id.ToString(),
            "success",
            occurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseAsync(
        Guid tenantId,
        Guid productId,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Un producto borrado a mano en QEP no tiene nada que soltar: quien llama igual quita sus
        // vínculos (Review Focus 4).
        var product = await repository.FindAsync(tenantId, new ProductId(productId), cancellationToken);
        if (product?.ExternalSource is null)
        {
            return;
        }

        product.ReleaseExternal(occurredAt);
        auditPublisher.Publish(
            tenantId, actorId, "catalog.product.updated", product.Id.ToString(), "success", occurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ReplaceImageAsync(
        Guid tenantId,
        Guid productId,
        Guid? previousFileId,
        Guid newFileId,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Sin ProductImageResolver: el archivo lo acaba de subir el camino de servidor de Storage para
        // este tenant y este producto; no viene de un cliente.
        var product = await repository.FindAsync(tenantId, new ProductId(productId), cancellationToken);
        var replaced = product?.ReplaceGalleryImage(previousFileId, newFileId, occurredAt) ?? false;

        if (replaced)
        {
            if (previousFileId is { } previous)
            {
                imageReleasePublisher.Publish(tenantId, [previous], occurredAt);
            }

            auditPublisher.Publish(
                tenantId, actorId, "catalog.product.updated", productId.ToString(), "success", occurredAt);
        }
        else
        {
            // No entró (o el producto ya no existe): sin esto quedaría Available y sin dueño para siempre.
            imageReleasePublisher.Publish(tenantId, [newFileId], occurredAt);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return replaced;
    }

    public async Task<ExternalDefaultTaxRates> FindDefaultTaxRatesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // D4: el IVA general del tenant, sin base mínima ni ciudad; la más antigua si hay varias.
        var candidates = (await taxRateRepository.ListAsync(tenantId, cancellationToken))
            .Where(rate => rate.IsActive
                && rate.Type == TaxCountries.IvaType
                && rate.MinimumBase is null
                && rate.CityId is null)
            .OrderBy(rate => rate.CreatedAt)
            .ThenBy(rate => rate.Id.Value)
            .ToArray();

        return new ExternalDefaultTaxRates(
            candidates.FirstOrDefault(rate => rate.Percentage == 19m)?.Id.Value,
            candidates.FirstOrDefault(rate => rate.Percentage == 0m)?.Id.Value);
    }

    public async Task<Guid?> FindVariantIdByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var product = await repository.FindVariantByCodeAsync(tenantId, trimmed, cancellationToken);
        return product?.Variants
            .FirstOrDefault(variant => string.Equals(variant.Code, trimmed, StringComparison.Ordinal))
            ?.Id.Value;
    }

    private sealed record ResolvedOptions(
        IReadOnlyList<Guid> OptionIds,
        IReadOnlyList<IReadOnlyList<Guid>> ValueIds,
        IReadOnlyDictionary<Guid, OptionDefinition> Library);

    /// <summary>
    /// Opciones y valores por nombre (§5.3): recortado y sin distinguir mayúsculas. La que no existe se
    /// crea activa, la inactiva se activa, y lo mismo con cada valor. Con seguimiento: los cambios salen
    /// en el mismo guardado que el producto.
    /// </summary>
    private async Task<ResolvedOptions> ResolveOptionsAsync(
        ExternalProductDraft draft,
        CancellationToken cancellationToken)
    {
        var library = await optionRepository.ListAsync(draft.TenantId, cancellationToken);
        var options = new List<OptionDefinition>(draft.Options.Count);

        for (var position = 0; position < draft.Options.Count; position++)
        {
            var name = draft.Options[position].Name.Trim();
            var values = draft.Options[position].Values
                .Concat(draft.Variants.Select(variant => position < variant.OptionValues.Count ? variant.OptionValues[position] : null))
                .OfType<string>()
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Una repetida en el borrador es la misma opción: el agregado la rechaza con option_duplicate
            // en vez de que se cree dos veces y choque el índice de la biblioteca.
            var repeated = options.Find(option => string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
            if (repeated is not null)
            {
                options.Add(repeated);
                continue;
            }

            var match = library.FirstOrDefault(option => string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
            OptionDefinition option;
            if (match is null)
            {
                option = OptionDefinition.Create(draft.TenantId, name, values, isActive: true, draft.OccurredAt);
                optionRepository.Add(option);
            }
            else
            {
                option = await optionRepository.FindAsync(draft.TenantId, match.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Option '{match.Id}' vanished while importing.");
                if (!option.IsActive)
                {
                    option.SetActive(true);
                }

                foreach (var value in values)
                {
                    var saved = FindValue(option, value);
                    if (saved is null)
                    {
                        option.AddValue(value);
                    }
                    else if (!saved.IsActive)
                    {
                        option.SetValueActive(saved.Id, true);
                    }
                }
            }

            options.Add(option);
        }

        return new ResolvedOptions(
            options.Select(option => option.Id.Value).ToArray(),
            draft.Variants.Select(variant => ValueIdsOf(options, variant)).ToArray(),
            options.ToDictionary(option => option.Id.Value));
    }

    // Una lista corta (falta un valor) la rechaza ProductVariantReferences con value_count_mismatch.
    private static List<Guid> ValueIdsOf(List<OptionDefinition> options, ExternalVariantDraft variant)
    {
        var ids = new List<Guid>(options.Count);
        for (var position = 0; position < options.Count && position < variant.OptionValues.Count; position++)
        {
            if (FindValue(options[position], variant.OptionValues[position].Trim()) is not { } value)
            {
                break;
            }

            ids.Add(value.Id);
        }

        return ids;
    }

    private static OptionValue? FindValue(OptionDefinition option, string value) =>
        option.Values.FirstOrDefault(saved => string.Equals(saved.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El código de barras de cada variante del borrador, o null si ya lo usa otro producto del tenant u
    /// otra variante del borrador: la variante entra sin él y su clave vuelve en <c>DroppedBarcodeKeys</c>
    /// (§5.3), en vez de tumbar el producto entero.
    /// </summary>
    private async Task<(string?[] Barcodes, IReadOnlyList<string> Dropped)> KeepAvailableBarcodesAsync(
        ExternalProductDraft draft,
        Product? product,
        CancellationToken cancellationToken)
    {
        var requested = draft.Variants
            .Select(variant => string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim())
            .ToArray();
        var own = product?.Variants.Select(variant => variant.Barcode).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var taken = await repository.FindExistingBarcodesAsync(
            draft.TenantId, requested.OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        var barcodes = new string?[requested.Length];
        for (var index = 0; index < requested.Length; index++)
        {
            if (requested[index] is not { } barcode)
            {
                continue;
            }

            if ((taken.Contains(barcode) && !own.Contains(barcode)) || !seen.Add(barcode))
            {
                dropped.Add(draft.Variants[index].Key);
                continue;
            }

            barcodes[index] = barcode;
        }

        return (barcodes, dropped);
    }

    // Sin escribir nada: las opciones se buscan en la biblioteca sin seguimiento. Una opción o un valor
    // que no existe es un cambio (se crearía).
    private async Task<bool> IsUnchangedAsync(
        Product product,
        ExternalProductDraft draft,
        string currency,
        Guid?[] targets,
        string?[] barcodes,
        Dictionary<Guid, ProductVariant> savedById,
        int keptInactive,
        CancellationToken cancellationToken)
    {
        if (product.IsActive != draft.IsActive
            || !string.Equals(draft.Name.Trim(), product.Name, StringComparison.Ordinal)
            || draft.Variants.Count != product.Variants.Count - keptInactive
            || targets.Any(target => target is null)
            || targets.Distinct().Count() != targets.Length)
        {
            return false;
        }

        var library = await optionRepository.ListAsync(draft.TenantId, cancellationToken);
        var options = new List<OptionDefinition>(draft.Options.Count);
        foreach (var draftOption in draft.Options)
        {
            var match = library.FirstOrDefault(option =>
                string.Equals(option.Name, draftOption.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            options.Add(match);
        }

        if (!options.Select(option => option.Id.Value).SequenceEqual(product.OptionIds))
        {
            return false;
        }

        for (var index = 0; index < draft.Variants.Count; index++)
        {
            var variant = draft.Variants[index];
            var saved = savedById[targets[index]!.Value];
            var price = saved.Prices.FirstOrDefault(candidate => candidate.Currency == currency);
            if (!saved.IsActive
                || !string.Equals(variant.Code.Trim(), saved.Code, StringComparison.Ordinal)
                || !string.Equals(barcodes[index], saved.Barcode, StringComparison.Ordinal)
                || price is null
                || price.Amount != variant.Price
                || !ValueIdsOf(options, variant).SequenceEqual(saved.OptionValueIds))
            {
                return false;
            }
        }

        return true;
    }

    private static Guid? GalleryFileOf(Product product, Guid? variantId)
    {
        if (variantId is not { } id
            || product.Variants.FirstOrDefault(variant => variant.Id.Value == id)?.GalleryImageId is not { } imageId)
        {
            return null;
        }

        return product.Gallery.FirstOrDefault(image => image.Id == imageId)?.FileId;
    }

    private static Dictionary<string, decimal> WithPrice(
        IReadOnlyDictionary<string, decimal> prices,
        string currency,
        decimal price) =>
        new(prices, StringComparer.Ordinal) { [currency] = price };

    // Una guardada conserva su id; una nueva se encuentra por código, que es único en el producto.
    private static Dictionary<string, Guid> KeysOf(
        ExternalProductDraft draft,
        Product product,
        Guid?[]? targets)
    {
        var byCode = product.Variants.ToDictionary(variant => variant.Code, variant => variant.Id.Value, StringComparer.Ordinal);
        var keys = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var index = 0; index < draft.Variants.Count; index++)
        {
            var variant = draft.Variants[index];
            if (targets?[index] is { } target)
            {
                keys[variant.Key] = target;
            }
            else if (byCode.TryGetValue(variant.Code.Trim(), out var id))
            {
                keys[variant.Key] = id;
            }
        }

        return keys;
    }
}
