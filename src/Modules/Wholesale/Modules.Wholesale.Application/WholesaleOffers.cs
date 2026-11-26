using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Fase 3: los términos de una relación <c>Active</c>, leídos para el comprador. <paramref name="AdvisorMembershipId"/> es el
/// asesor del distribuidor (ruling X5: un solo nombre); <paramref name="VisibleCategoryIds"/> <c>null</c> es «todo el catálogo
/// activo», si no, las categorías de la lista (sus descendientes también se ven). <paramref name="PriceListName"/> es la
/// tarjeta «Tarifa» de la adenda del contrato. Spec 2026-11-20: <paramref name="HiddenCategoryIds"/> y
/// <paramref name="HiddenProductIds"/> son lo que la lista excluye (vacías cuando no hay): lo excluido gana sobre el alcance, una
/// categoría excluida oculta también sus descendientes y un producto excluido, todas sus variantes.
/// </summary>
public sealed record WholesaleTerms(
    Guid DistributorId,
    Guid CustomerId,
    Guid AdvisorMembershipId,
    Guid BillingCompanyId,
    string Currency,
    decimal DiscountPercent,
    IReadOnlyCollection<Guid>? VisibleCategoryIds,
    string PriceListName,
    IReadOnlyCollection<Guid> HiddenCategoryIds,
    IReadOnlyCollection<Guid> HiddenProductIds,
    bool ShowPrices = true)
{
    /// <summary>Spec 2026-11-22: la lista de Catalog con la que se precia; la usa <see cref="IWholesalePriceLists.PriceAsync"/>.</summary>
    internal WholesalePriceList? List { get; init; }
}

/// <summary>Una variante con su precio de lista para el distribuidor (IVA incluido, con el redondeo del mayorista).</summary>
public sealed record WholesaleOffer(
    Guid VariantId,
    string Code,
    string Name,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal ListPrice,
    decimal TaxRatePercent,
    Guid ProductId = default,
    string ProductName = "",
    string? ImageUrl = null);

/// <summary>
/// Las ofertas de un pedido que ya existe (<see cref="WholesaleOffers.OffersForOrderAsync"/>) y el flag vivo de la lista del
/// distribuidor (spec 2026-11-19, D-14). Sin relación o sin lista, vacío y <see cref="ShowPrices"/> en <c>false</c>: sin
/// información la orden de compra queda sin precio y lo recibe al aprobar, nunca un precio que no se debía ver.
/// </summary>
public sealed record WholesaleOrderOffers(IReadOnlyDictionary<Guid, WholesaleOffer> Offers, bool ShowPrices);

/// <summary>Una página del catálogo visto por el distribuidor, con la lista que la precia (adenda: <c>priceList</c>).</summary>
public sealed record WholesaleOfferPage(
    string Currency,
    string PriceListName,
    decimal DiscountPercent,
    IReadOnlyList<WholesaleOffer> Items,
    int Total,
    bool ShowPrices = true);

/// <summary>
/// Spec 2026-11-17 (mayorista, fase 3): todo lo que un tenant lee del catálogo de otro pasa por acá. Quien llama es un adaptador
/// del comprador o del mayorista; ninguno de los dos ids sale de un cuerpo (lo garantiza la ruta autorizada que los llama).
/// <para>Acceso (como la aceptación, rulings W14/W16 de fase 2): la relación tiene que existir entre <b>ese</b> par de tenants y
/// estar <c>Active</c>, y el mayorista tiene que estar <c>Active</c> con el módulo <c>wholesale</c>; si no, 403
/// <c>wholesale.relationship.inactive</c>, sin decir cuál de las condiciones falló.</para>
/// <para>La única excepción es <see cref="ListPricesForOrderAsync"/> (D-5): la lee el mayorista sobre su propio pedido.</para>
/// </summary>
public sealed class WholesaleOffers(
    IDistributorRepository distributors,
    IWholesalePriceLists priceLists,
    ITenantDefaultCurrency defaultCurrency,
    IWholesaleCatalogReader catalog,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules)
{
    public async Task<WholesaleTerms> ResolveTermsAsync(
        Guid wholesalerTenantId, Guid buyerTenantId, CancellationToken cancellationToken)
    {
        var distributor = await distributors.FindByTenantsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        if (distributor is not { Status: DistributorStatus.Active, CustomerId: { } customerId }
            || distributor.TenantId != wholesalerTenantId
            || distributor.DistributorTenantId != buyerTenantId)
        {
            throw WholesaleConflicts.RelationshipInactive();
        }

        if (await tenantDirectory.GetStatusAsync(new TenantId(wholesalerTenantId), cancellationToken) != TenantStatus.Active
            || await tenantModules.FindAsync(wholesalerTenantId, cancellationToken) is not { } modules
            || !modules.IsEnabled(TenantModuleKeys.Wholesale))
        {
            throw WholesaleConflicts.RelationshipInactive();
        }

        // Spec 2026-11-22 (D-8): la lista vive en Catalog sin FK desde acá. Una en uso no se puede borrar
        // (catalog.price_list.in_use); si aun así falta —o el distribuidor viene de antes de la unificación—, no hay precio que
        // mostrar: el mismo 403 de siempre hasta que alguien le asigne una.
        var list = await priceLists.GetAsync(wholesalerTenantId, distributor.PriceListId, cancellationToken)
            ?? throw WholesaleConflicts.RelationshipInactive();
        var currency = await defaultCurrency.GetAsync(wholesalerTenantId, cancellationToken);

        return new WholesaleTerms(
            distributor.Id.Value,
            customerId,
            distributor.AdvisorMemberId,
            distributor.BillingCompanyId,
            currency,
            DiscountOf(list.AdjustmentPercent),
            list.VisibleCategoryIds,
            list.Name,
            list.HiddenCategoryIds,
            list.HiddenProductIds,
            list.ShowPrices)
        {
            List = list,
        };
    }

    /// <summary>
    /// El árbol de categorías que la lista del distribuidor le deja ver, plano y en el orden del lector. Con una lista de
    /// categorías: ellas más sus descendientes; una cuyo padre queda fuera sale como raíz (<c>ParentId = null</c>), y un
    /// ancestro que no está en la lista no se devuelve.
    /// </summary>
    public async Task<IReadOnlyList<WholesaleCategoryNode>> ListVisibleCategoriesAsync(
        Guid wholesalerTenantId, Guid buyerTenantId, CancellationToken cancellationToken)
    {
        var terms = await ResolveTermsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        return await VisibleTreeAsync(wholesalerTenantId, terms, cancellationToken);
    }

    public async Task<WholesaleOfferPage> SearchAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string? search,
        Guid? categoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var terms = await ResolveTermsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        IReadOnlyCollection<Guid>? categoryIds = terms.VisibleCategoryIds;
        if (categoryId is { } chosen)
        {
            // La validación contra el árbol visible es lo que garantiza la lista; al lector sólo le llega la elegida (él expande
            // sus descendientes), así que el filtro nunca amplía lo que la lista permite.
            var visible = await VisibleTreeAsync(wholesalerTenantId, terms, cancellationToken);
            if (visible.All(node => node.Id != chosen))
            {
                throw new WholesaleDomainException(
                    WholesaleErrorCodes.CategoryNotVisible, "La categoría ya no está disponible para ti.");
            }

            categoryIds = [chosen];
        }

        var (items, total) = await catalog.SearchSellableAsync(
            wholesalerTenantId, terms.Currency, categoryIds, terms.HiddenCategoryIds, terms.HiddenProductIds, search, page, pageSize,
            cancellationToken);
        var prices = await PricesAsync(wholesalerTenantId, terms.List!, terms.Currency, items, cancellationToken);

        return new WholesaleOfferPage(
            terms.Currency,
            terms.PriceListName,
            terms.DiscountPercent,
            items.Select(item => OfferOf(item, terms.List!, prices)).ToList(),
            total,
            terms.ShowPrices);
    }

    private async Task<IReadOnlyList<WholesaleCategoryNode>> VisibleTreeAsync(
        Guid wholesalerTenantId, WholesaleTerms terms, CancellationToken cancellationToken)
    {
        var all = await catalog.ListCategoriesAsync(wholesalerTenantId, cancellationToken);
        var visible = terms.VisibleCategoryIds is { } listed ? DescendantsOf(all, listed) : all.Select(node => node.Id).ToHashSet();
        if (terms.HiddenCategoryIds.Count > 0)
        {
            // Spec 2026-11-20: lo excluido gana. Se quita la categoría y todo su subárbol; el padre no se toca.
            visible.ExceptWith(DescendantsOf(all, terms.HiddenCategoryIds));
        }

        // Un nodo cuyo padre no se ve (o no existe) sale como raíz: el árbol que llega al comprador nunca apunta afuera de sí mismo.
        return all
            .Where(node => visible.Contains(node.Id))
            .Select(node => node.ParentId is { } parent && !visible.Contains(parent) ? node with { ParentId = null } : node)
            .ToList();
    }

    /// <summary>BFS desde las categorías de la lista que aún existen; el conjunto de visitados corta un ciclo.</summary>
    private static HashSet<Guid> DescendantsOf(IReadOnlyList<WholesaleCategoryNode> all, IReadOnlyCollection<Guid> listed)
    {
        var childrenByParent = all.Where(node => node.ParentId is not null).ToLookup(node => node.ParentId!.Value, node => node.Id);
        var inTree = all.Select(node => node.Id).ToHashSet();
        var visible = new HashSet<Guid>();
        var queue = new Queue<Guid>(listed.Where(inTree.Contains).Where(visible.Add));
        while (queue.TryDequeue(out var current))
        {
            foreach (var child in childrenByParent[current].Where(visible.Add))
            {
                queue.Enqueue(child);
            }
        }

        return visible;
    }

    /// <summary>
    /// El precio de cada variante pedida. Un id que no vuelve del catálogo (inactivo, sin precio, ajeno) o que queda fuera de
    /// las categorías de la lista es 422 <c>wholesale.product.not_visible</c>; el mensaje nombra el código cuando se conoce.
    /// </summary>
    public async Task<(WholesaleTerms Terms, IReadOnlyDictionary<Guid, WholesaleOffer> Offers)> PriceVisibleAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        var terms = await ResolveTermsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        var found = await catalog.FindSellableAsync(wholesalerTenantId, terms.Currency, variantIds, cancellationToken);
        var prices = await PricesAsync(wholesalerTenantId, terms.List!, terms.Currency, found.Values, cancellationToken);
        var visible = terms.VisibleCategoryIds?.ToHashSet();
        var hiddenCategories = terms.HiddenCategoryIds.ToHashSet();
        var hiddenProducts = terms.HiddenProductIds.ToHashSet();

        var offers = new Dictionary<Guid, WholesaleOffer>();
        foreach (var id in variantIds.Distinct())
        {
            if (!found.TryGetValue(id, out var item))
            {
                throw NotVisible(null);
            }

            if (visible is not null && !item.CategoryPath.Any(visible.Contains))
            {
                throw NotVisible(item.Code);
            }

            // Spec 2026-11-20: el camino de la variante es su categoría y sus ancestros, así que tocar una categoría excluida es
            // estar en ella o debajo de ella.
            if (hiddenProducts.Contains(item.ProductId) || item.CategoryPath.Any(hiddenCategories.Contains))
            {
                throw NotVisible(item.Code);
            }

            offers[id] = OfferOf(item, terms.List!, prices);
        }

        return (terms, offers);
    }

    /// <summary>
    /// D-5: el precio de lista que tendría cada variante para ese distribuidor, sin mirar el estado de la relación ni la
    /// visibilidad (el mayorista edita su propio pedido). Sin relación, vacío; una variante sin precio en
    /// <paramref name="currency"/> no viene.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> ListPricesForOrderAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken) =>
        (await OffersForOrderAsync(wholesalerTenantId, buyerTenantId, currency, variantIds, cancellationToken)).Offers
            .ToDictionary(pair => pair.Key, pair => pair.Value.ListPrice);

    /// <summary>
    /// Lo mismo que <see cref="ListPricesForOrderAsync"/>, con la oferta entera (precio base y descuento): el reintento de un
    /// envío que encuentra el pedido ya creado (D-8) pinta «base y descuento» sin volver a validar la relación.
    /// </summary>
    public async Task<WholesaleOrderOffers> OffersForOrderAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        var distributor = await distributors.FindByTenantsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        if (distributor is null)
        {
            return new WholesaleOrderOffers(new Dictionary<Guid, WholesaleOffer>(), false);
        }

        var list = await priceLists.GetAsync(wholesalerTenantId, distributor.PriceListId, cancellationToken);
        if (list is null)
        {
            return new WholesaleOrderOffers(new Dictionary<Guid, WholesaleOffer>(), false);
        }

        if (variantIds.Count == 0)
        {
            return new WholesaleOrderOffers(new Dictionary<Guid, WholesaleOffer>(), list.ShowPrices);
        }

        var found = await catalog.FindSellableAsync(wholesalerTenantId, currency, variantIds, cancellationToken);
        var prices = await PricesAsync(wholesalerTenantId, list, currency, found.Values, cancellationToken);
        return new WholesaleOrderOffers(
            found.ToDictionary(pair => pair.Key, pair => OfferOf(pair.Value, list, prices)),
            list.ShowPrices);
    }

    /// <summary>
    /// Spec 2026-11-22: el descuento que pinta «base y descuento» es el ajuste con el signo al revés (un recargo es un descuento
    /// negativo). La columna de la orden de compra es <c>numeric(5,2)</c>, así que se acota a lo que cabe.
    /// </summary>
    internal static decimal DiscountOf(decimal adjustmentPercent) => Math.Clamp(-adjustmentPercent, MinDiscount, MaxDiscount);

    private const decimal MinDiscount = -999.99m;

    private const decimal MaxDiscount = 100m;

    private Task<IReadOnlyDictionary<Guid, WholesaleListedPrice>> PricesAsync(
        Guid wholesalerTenantId,
        WholesalePriceList list,
        string currency,
        IEnumerable<WholesaleSellableVariant> items,
        CancellationToken cancellationToken) =>
        priceLists.PriceAsync(
            wholesalerTenantId,
            list,
            currency,
            items.GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First().BasePrice),
            cancellationToken);

    /// <summary>
    /// Con precio fijo, el descuento se deriva del base y el fijo (dos decimales, acotado como <see cref="DiscountOf"/>); si no
    /// cabe, la línea se pinta sin descuento y con el fijo como base, para que «base y descuento» siempre cierre.
    /// </summary>
    private static WholesaleOffer OfferOf(
        WholesaleSellableVariant item, WholesalePriceList list, IReadOnlyDictionary<Guid, WholesaleListedPrice> prices)
    {
        var listed = prices.TryGetValue(item.VariantId, out var found) ? found : new WholesaleListedPrice(item.BasePrice, true);
        var basePrice = item.BasePrice;
        decimal discount;
        if (!listed.IsFixed)
        {
            discount = DiscountOf(list.AdjustmentPercent);
        }
        else
        {
            var derived = basePrice == 0m ? 0m : Math.Round((1 - (listed.Price / basePrice)) * 100m, 2, MidpointRounding.AwayFromZero);
            if (derived is < MinDiscount or > MaxDiscount)
            {
                (basePrice, discount) = (listed.Price, 0m);
            }
            else
            {
                discount = derived;
            }
        }

        return new(
            item.VariantId,
            item.Code,
            item.Name,
            basePrice,
            discount,
            listed.Price,
            item.TaxRatePercent,
            item.ProductId,
            item.ProductName,
            item.ImageUrl);
    }

    private static WholesaleDomainException NotVisible(string? code) => new(
        WholesaleErrorCodes.ProductNotVisible,
        code is null
            ? "Uno de los productos ya no está disponible para ti."
            : $"El producto {code} ya no está disponible para ti.");
}
