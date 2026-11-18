using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Fase 3: los términos de una relación <c>Active</c>, leídos para el comprador. <paramref name="AdvisorMembershipId"/> es el
/// asesor del distribuidor (ruling X5: un solo nombre); <paramref name="VisibleCategoryIds"/> <c>null</c> es «todo el catálogo
/// activo», si no, las categorías de la lista (sus descendientes también se ven). <paramref name="PriceListName"/> es la
/// tarjeta «Tarifa» de la adenda del contrato.
/// </summary>
public sealed record WholesaleTerms(
    Guid DistributorId,
    Guid CustomerId,
    Guid AdvisorMembershipId,
    Guid BillingCompanyId,
    string Currency,
    decimal DiscountPercent,
    IReadOnlyCollection<Guid>? VisibleCategoryIds,
    string PriceListName);

/// <summary>Una variante con su precio de lista para el distribuidor (IVA incluido, con el redondeo del mayorista).</summary>
public sealed record WholesaleOffer(
    Guid VariantId,
    string Code,
    string Name,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal ListPrice,
    decimal TaxRatePercent);

/// <summary>Una página del catálogo visto por el distribuidor, con la lista que la precia (adenda: <c>priceList</c>).</summary>
public sealed record WholesaleOfferPage(
    string Currency,
    string PriceListName,
    decimal DiscountPercent,
    IReadOnlyList<WholesaleOffer> Items,
    int Total);

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
    IPriceListRepository priceLists,
    ITenantDefaultCurrency defaultCurrency,
    ITenantPriceRounding priceRounding,
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

        // Una lista en uso no se puede borrar (wholesale.price_list.in_use); si aun así falta, no hay precio que mostrar.
        var list = await priceLists.GetAsync(wholesalerTenantId, distributor.PriceListId, cancellationToken)
            ?? throw WholesaleConflicts.RelationshipInactive();
        var currency = await defaultCurrency.GetAsync(wholesalerTenantId, cancellationToken);

        return new WholesaleTerms(
            distributor.Id.Value,
            customerId,
            distributor.AdvisorMemberId,
            distributor.BillingCompanyId,
            currency,
            list.DiscountPercent,
            list.Visibility == PriceListVisibility.AllActive ? null : list.CategoryIds.ToArray(),
            list.Name);
    }

    public async Task<WholesaleOfferPage> SearchAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var terms = await ResolveTermsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        var rounding = await priceRounding.GetAsync(wholesalerTenantId, terms.Currency, cancellationToken);
        var (items, total) = await catalog.SearchSellableAsync(
            wholesalerTenantId, terms.Currency, terms.VisibleCategoryIds, search, page, pageSize, cancellationToken);

        return new WholesaleOfferPage(
            terms.Currency,
            terms.PriceListName,
            terms.DiscountPercent,
            items.Select(item => OfferOf(item, terms.DiscountPercent, rounding)).ToList(),
            total);
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
        var rounding = await priceRounding.GetAsync(wholesalerTenantId, terms.Currency, cancellationToken);
        var found = await catalog.FindSellableAsync(wholesalerTenantId, terms.Currency, variantIds, cancellationToken);
        var visible = terms.VisibleCategoryIds?.ToHashSet();

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

            offers[id] = OfferOf(item, terms.DiscountPercent, rounding);
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
        CancellationToken cancellationToken)
    {
        var distributor = await distributors.FindByTenantsAsync(wholesalerTenantId, buyerTenantId, cancellationToken);
        if (distributor is null || variantIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var list = await priceLists.GetAsync(wholesalerTenantId, distributor.PriceListId, cancellationToken);
        if (list is null)
        {
            return new Dictionary<Guid, decimal>();
        }

        var rounding = await priceRounding.GetAsync(wholesalerTenantId, currency, cancellationToken);
        var found = await catalog.FindSellableAsync(wholesalerTenantId, currency, variantIds, cancellationToken);
        return found.ToDictionary(
            pair => pair.Key,
            pair => WholesalePricing.ListPrice(pair.Value.BasePrice, list.DiscountPercent, rounding));
    }

    private static WholesaleOffer OfferOf(
        WholesaleSellableVariant item, decimal discountPercent, PriceRounding rounding) =>
        new(
            item.VariantId,
            item.Code,
            item.Name,
            item.BasePrice,
            discountPercent,
            WholesalePricing.ListPrice(item.BasePrice, discountPercent, rounding),
            item.TaxRatePercent);

    private static WholesaleDomainException NotVisible(string? code) => new(
        WholesaleErrorCodes.ProductNotVisible,
        code is null
            ? "Uno de los productos ya no está disponible para ti."
            : $"El producto {code} ya no está disponible para ti.");
}
