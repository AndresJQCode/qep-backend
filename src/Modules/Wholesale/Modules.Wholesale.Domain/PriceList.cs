namespace Modules.Wholesale.Domain;

/// <summary>
/// Una lista de precios del mayorista: un descuento sobre el precio base y qué productos ve el distribuidor. Toda mutación
/// sube <see cref="Version"/> (el <c>If-Match</c> del endpoint). Las reglas de texto y de número dan un código del dominio;
/// el validador de Application da el campo.
/// </summary>
public sealed class PriceList
{
    public const int NameMaxLength = 120;

    public const int MaxCategories = 200;

    /// <summary>Spec 2026-11-20: tope de categorías excluidas por lista.</summary>
    public const int MaxHiddenCategories = 200;

    /// <summary>Spec 2026-11-20: tope de productos excluidos por lista.</summary>
    public const int MaxHiddenProducts = 1000;

    private const int DiscountDecimals = 2;

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

    /// <summary>Recortado. Único por tenant sin importar mayúsculas (D-12): lo sostiene un índice de la base.</summary>
    public string Name { get; private set; }

    /// <summary>0-100, a lo sumo dos decimales.</summary>
    public decimal DiscountPercent { get; private set; }

    public PriceListVisibility Visibility { get; private set; }

    /// <summary>Con <see cref="PriceListVisibility.Categories"/>, los ids sin repetir en el orden recibido; con
    /// <see cref="PriceListVisibility.AllActive"/>, vacía. Que existan en el tenant lo valida Application (D-9).</summary>
    public IReadOnlyList<Guid> CategoryIds => _categoryIds;

    /// <summary>Spec 2026-11-19: con <c>false</c> el distribuidor de esta lista no recibe montos (catálogo y vista previa de Compras). Por defecto <c>true</c>.</summary>
    public bool ShowPrices { get; private set; } = true;

    /// <summary>
    /// Spec 2026-11-20: lo que el distribuidor no ve aunque esté en el alcance de la lista, categorías primero y luego productos,
    /// sin repetir. Se guarda en dos columnas, pero el dominio expone una sola lista.
    /// </summary>
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

    public static PriceList Create(
        Guid tenantId,
        string name,
        decimal discountPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds,
        DateTimeOffset now,
        bool showPrices = true,
        IReadOnlyCollection<PriceListExclusion>? exclusions = null)
    {
        var terms = Normalize(name, discountPercent, visibility, categoryIds);
        var (hiddenCategories, hiddenProducts) = NormalizeExclusions(exclusions);
        return new PriceList
        {
            Id = PriceListId.New(),
            TenantId = tenantId,
            Name = terms.Name,
            DiscountPercent = discountPercent,
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
        decimal discountPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds,
        DateTimeOffset now,
        bool showPrices,
        IReadOnlyCollection<PriceListExclusion>? exclusions = null)
    {
        // Se valida todo antes de asignar: un cambio rechazado no deja la lista a medias.
        var terms = Normalize(name, discountPercent, visibility, categoryIds);
        var (hiddenCategories, hiddenProducts) = NormalizeExclusions(exclusions);
        Name = terms.Name;
        DiscountPercent = discountPercent;
        Visibility = visibility;
        ShowPrices = showPrices;
        _categoryIds = terms.CategoryIds;
        _excludedCategoryIds = hiddenCategories;
        _excludedProductIds = hiddenProducts;
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
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListExclusionsTooMany,
                $"Una lista oculta a lo sumo {MaxHiddenCategories} categorías y {MaxHiddenProducts} productos.");
        }

        return (categories, products);
    }

    private static (string Name, List<Guid> CategoryIds) Normalize(
        string name,
        decimal discountPercent,
        PriceListVisibility visibility,
        IReadOnlyCollection<Guid> categoryIds)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListNameRequired, "La lista de precios necesita un nombre.");
        }

        if (trimmed.Length > NameMaxLength)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListNameTooLong,
                $"El nombre de la lista tiene a lo sumo {NameMaxLength} caracteres.");
        }

        if (discountPercent is < 0m or > 100m || decimal.Round(discountPercent, DiscountDecimals) != discountPercent)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListDiscountInvalid,
                "El descuento va de 0 a 100, con a lo sumo dos decimales.");
        }

        if (!Enum.IsDefined(visibility))
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListVisibilityInvalid, "La visibilidad de la lista no es válida.");
        }

        ArgumentNullException.ThrowIfNull(categoryIds);
        if (visibility == PriceListVisibility.AllActive)
        {
            return (trimmed, []);
        }

        var distinct = categoryIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListCategoriesRequired, "Elige al menos una categoría.");
        }

        if (distinct.Count > MaxCategories)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.PriceListCategoriesTooMany,
                $"Una lista tiene a lo sumo {MaxCategories} categorías.");
        }

        return (trimmed, distinct);
    }
}
