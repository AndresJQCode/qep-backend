using BuildingBlocks.Domain.Pricing;

namespace Modules.Catalog.Domain;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal): una lista de precios del tenant. Precia un canal (<see cref="ChannelPriceList"/>)
/// o un distribuidor (Mayorista guarda el id). Reemplaza a la lista mayorista (D-7): los términos de distribuidor —visibilidad,
/// exclusiones y <see cref="ShowPrices"/>— viven acá pero sólo los lee el catálogo del distribuidor; POS, tienda y cotizaciones los
/// ignoran. Toda mutación sube <see cref="Version"/>, también la de sus precios fijos (<see cref="TouchOverrides"/>).
/// </summary>
public sealed class PriceList
{
    public const int NameMaxLength = 120;

    public const int MaxCategories = 200;

    public const int MaxHiddenCategories = 200;

    public const int MaxHiddenProducts = 1000;

    /// <summary>D-3: −100 deja el precio en cero; el tope es el de la columna <c>numeric(5,2)</c>.</summary>
    public const decimal MinAdjustment = -100m;

    public const decimal MaxAdjustment = 999.99m;

    private const int AdjustmentDecimals = 2;

    private List<Guid> _categoryIds = [];

    private List<Guid> _excludedCategoryIds = [];

    private List<Guid> _excludedProductIds = [];

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private PriceList()
    {
        Name = string.Empty;
    }

    public PriceListId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Recortado. Único por tenant sin importar mayúsculas: lo sostiene un índice de la base.</summary>
    public string Name { get; private set; }

    /// <summary>D-3: con signo. Negativo es descuento sobre el precio base, positivo recargo. A lo sumo dos decimales.</summary>
    public decimal AdjustmentPercent { get; private set; }

    public PriceListVisibility Visibility { get; private set; }

    /// <summary>Con <see cref="PriceListVisibility.Categories"/>, los ids sin repetir en el orden recibido; con
    /// <see cref="PriceListVisibility.AllActive"/>, vacía.</summary>
    public IReadOnlyList<Guid> CategoryIds => _categoryIds;

    /// <summary>Con <c>false</c> el distribuidor de esta lista no recibe montos. Por defecto <c>true</c>. Los canales no lo leen.</summary>
    public bool ShowPrices { get; private set; } = true;

    /// <summary>Lo que el distribuidor no ve aunque esté en el alcance de la lista, categorías primero y luego productos.</summary>
    public IReadOnlyList<PriceListExclusion> Exclusions =>
        [
            .. _excludedCategoryIds.Select(id => new PriceListExclusion(PriceListExclusionKind.Category, id)),
            .. _excludedProductIds.Select(id => new PriceListExclusion(PriceListExclusionKind.Product, id)),
        ];

    public IReadOnlyList<Guid> ExcludedCategoryIds => _excludedCategoryIds;

    public IReadOnlyList<Guid> ExcludedProductIds => _excludedProductIds;

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// D-2: el precio de una variante en una moneda con esta lista. La única copia de la regla: los canales y el distribuidor
    /// pasan por acá.
    /// <list type="number">
    /// <item>Sin precio base en la moneda no hay precio, ni siquiera con precio fijo: un precio fijo sólo reemplaza. Así el
    /// «tiene precio en la moneda» que filtran en SQL la tienda, el POS y el mayorista vale igual con lista.</item>
    /// <item>Con precio fijo, ése, tal cual (O-7).</item>
    /// <item>Si no, el base con el ajuste, redondeado con la regla de la moneda.</item>
    /// </list>
    /// </summary>
    public static decimal? PriceFor(decimal? basePrice, decimal adjustmentPercent, decimal? overrideAmount, PriceRounding rounding)
    {
        if (basePrice is not { } price)
        {
            return null;
        }

        if (overrideAmount is { } fixedPrice)
        {
            return fixedPrice;
        }

        return adjustmentPercent == 0m ? price : rounding.Apply(price * (1 + (adjustmentPercent / 100m)));
    }

    public static PriceList Create(
        Guid tenantId,
        string name,
        decimal adjustmentPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds,
        DateTimeOffset now,
        bool showPrices = true,
        IReadOnlyCollection<PriceListExclusion>? exclusions = null)
    {
        var terms = Normalize(name, adjustmentPercent, visibility, categoryIds);
        var (hiddenCategories, hiddenProducts) = NormalizeExclusions(exclusions);
        return new PriceList
        {
            Id = PriceListId.New(),
            TenantId = tenantId,
            Name = terms.Name,
            AdjustmentPercent = adjustmentPercent,
            Visibility = visibility,
            ShowPrices = showPrices,
            _categoryIds = terms.CategoryIds,
            _excludedCategoryIds = hiddenCategories,
            _excludedProductIds = hiddenProducts,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(
        string name,
        decimal adjustmentPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds,
        DateTimeOffset now,
        bool showPrices,
        IReadOnlyCollection<PriceListExclusion>? exclusions = null)
    {
        // Se valida todo antes de asignar: un cambio rechazado no deja la lista a medias.
        var terms = Normalize(name, adjustmentPercent, visibility, categoryIds);
        var (hiddenCategories, hiddenProducts) = NormalizeExclusions(exclusions);
        Name = terms.Name;
        AdjustmentPercent = adjustmentPercent;
        Visibility = visibility;
        ShowPrices = showPrices;
        _categoryIds = terms.CategoryIds;
        _excludedCategoryIds = hiddenCategories;
        _excludedProductIds = hiddenProducts;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>D-9: los precios fijos viven fuera del agregado, pero cambiarlos es cambiar la lista.</summary>
    public void TouchOverrides(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Sin repetir y en el orden recibido; con más de los topes, el dominio da el código y el validador el campo.</summary>
    private static (List<Guid> Categories, List<Guid> Products) NormalizeExclusions(
        IReadOnlyCollection<PriceListExclusion>? exclusions)
    {
        var distinct = (exclusions ?? []).Distinct().ToList();
        var categories = distinct.Where(item => item.Kind == PriceListExclusionKind.Category).Select(item => item.Id).ToList();
        var products = distinct.Where(item => item.Kind == PriceListExclusionKind.Product).Select(item => item.Id).ToList();
        if (categories.Count > MaxHiddenCategories || products.Count > MaxHiddenProducts)
        {
            throw new CatalogDomainException(
                "catalog.price_list.exclusions_too_many",
                $"A price list hides at most {MaxHiddenCategories} categories and {MaxHiddenProducts} products.");
        }

        return (categories, products);
    }

    private static (string Name, List<Guid> CategoryIds) Normalize(
        string name,
        decimal adjustmentPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new CatalogDomainException("catalog.price_list.name_required", "The price list needs a name.");
        }

        if (trimmed.Length > NameMaxLength)
        {
            throw new CatalogDomainException(
                "catalog.price_list.name_too_long", $"The price list name has at most {NameMaxLength} characters.");
        }

        if (adjustmentPercent < MinAdjustment
            || adjustmentPercent > MaxAdjustment
            || decimal.Round(adjustmentPercent, AdjustmentDecimals) != adjustmentPercent)
        {
            throw new CatalogDomainException(
                "catalog.price_list.adjustment_out_of_range",
                $"The adjustment goes from {MinAdjustment} to {MaxAdjustment}, with at most two decimals.");
        }

        if (!Enum.IsDefined(visibility))
        {
            throw new CatalogDomainException("catalog.price_list.visibility_invalid", "The price list visibility is not valid.");
        }

        ArgumentNullException.ThrowIfNull(categoryIds);
        if (visibility == PriceListVisibility.AllActive)
        {
            return (trimmed, []);
        }

        var distinct = categoryIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            throw new CatalogDomainException("catalog.price_list.categories_required", "Choose at least one category.");
        }

        if (distinct.Count > MaxCategories)
        {
            throw new CatalogDomainException(
                "catalog.price_list.categories_too_many", $"A price list has at most {MaxCategories} categories.");
        }

        return (trimmed, distinct);
    }
}
