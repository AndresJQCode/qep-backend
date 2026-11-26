using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Spec 2026-11-22 (listas de precios por canal): puertos, contrato y reglas de escritura de las listas. Los términos de
// distribuidor (visibilidad, exclusiones, mostrar precios) vienen de la lista mayorista de 2026-11-17/19/20 y conservan sus reglas.

/// <summary>Las listas, sus precios fijos y la asignación de canales. Todo método recibe el tenant.</summary>
public interface IPriceListRepository
{
    /// <summary>Con tracking: la usan las escrituras.</summary>
    Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Contra <c>lower(name)</c>, la llave del índice único.</summary>
    Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken cancellationToken);

    void Add(PriceList list);

    void Remove(PriceList list);

    /// <summary>Cuántos precios fijos tiene cada lista del tenant; una sin ninguno no viene.</summary>
    Task<IReadOnlyDictionary<PriceListId, int>> CountOverridesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de los precios fijos de la lista, por producto, variante y moneda. <paramref name="search"/> busca en el
    /// nombre del producto y en el código de la variante, sin importar mayúsculas.
    /// </summary>
    Task<(IReadOnlyList<PriceListOverrideRow> Items, int Total)> PageOverridesAsync(
        Guid tenantId, PriceListId id, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Con tracking: los precios fijos guardados de esas variantes, en cualquier moneda.</summary>
    Task<IReadOnlyList<PriceListOverride>> FindOverridesForUpdateAsync(
        Guid tenantId, PriceListId id, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken);

    /// <summary>Los precios fijos de esas variantes, sin tracking, por variante y moneda. Lo usa el resolutor de precios.</summary>
    Task<IReadOnlyDictionary<(Guid VariantId, string Currency), decimal>> FindOverrideAmountsAsync(
        Guid tenantId, PriceListId id, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Para la respuesta del lote: cada variante con su producto, código, nombre visible y precio base por moneda.</summary>
    Task<IReadOnlyDictionary<Guid, PriceListVariantRef>> FindVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    void AddOverride(PriceListOverride item);

    void RemoveOverride(PriceListOverride item);

    /// <summary>Con tracking: las asignaciones de canal del tenant.</summary>
    Task<IReadOnlyList<ChannelPriceList>> ListChannelsAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddChannel(ChannelPriceList assignment);

    void RemoveChannel(ChannelPriceList assignment);
}

/// <summary>Una variante del tenant como la pinta la tabla de precios fijos.</summary>
public sealed record PriceListVariantRef(
    Guid VariantId, Guid ProductId, string Code, string Name, IReadOnlyDictionary<string, decimal> BasePrices);

/// <summary>Una fila de precio fijo ya con lo que la pantalla dibuja.</summary>
public sealed record PriceListOverrideRow(
    Guid VariantId, Guid ProductId, string Code, string Name, string Currency, decimal? BasePrice, decimal Amount);

/// <summary>
/// D-11: cuántos distribuidores usan cada lista. Catalog no ve Mayorista: lo implementa Bootstrapper. Cuenta los distribuidores
/// en cualquier estado, porque es lo que impide borrar la lista.
/// </summary>
public interface IPriceListUsage
{
    Task<IReadOnlyDictionary<Guid, int>> CountDistributorsByPriceListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una categoría visible de la lista con su nombre ya resuelto: la pantalla no cruza ids con el catálogo.</summary>
public sealed record PriceListCategoryDto(Guid Id, string Name);

/// <summary>
/// Una exclusión con su nombre ya resuelto. <c>Kind</c> es <c>Category</c> o <c>Product</c>; <c>Code</c> (el de su única
/// variante) y <c>CategoryName</c> sólo para <c>Product</c>, y pueden ser <c>null</c>.
/// </summary>
public sealed record PriceListExclusionDto(string Kind, Guid Id, string Name, string? Code, string? CategoryName);

/// <summary>
/// Una fila de la lista de listas. Trae <c>Channels</c> y <c>DistributorCount</c> para que la pantalla diga «usada en POS y en 3
/// distribuidores» sin cruzar dos respuestas, y <c>OverrideCount</c> para «12 precios fijos» sin pedir la página. Los canales
/// viajan por nombre, en el orden del enum.
/// </summary>
public sealed record PriceListSummaryDto(
    Guid Id,
    string Name,
    decimal AdjustmentPercent,
    string Visibility,
    int CategoryCount,
    IReadOnlyList<PriceListCategoryDto> Categories,
    int OverrideCount,
    IReadOnlyList<string> Channels,
    int DistributorCount,
    long Version,
    DateTimeOffset CreatedAt,
    bool ShowPrices,
    int HiddenCategoryCount,
    int HiddenProductCount);

/// <summary>
/// La lista completa para el formulario. <c>Categories</c> trae sólo las que siguen existiendo, por nombre: el siguiente
/// <c>PUT</c> guarda el conjunto limpio. Los precios fijos no vienen acá (D-9): se piden paginados.
/// </summary>
public sealed record PriceListDetailDto(
    Guid Id,
    string Name,
    decimal AdjustmentPercent,
    string Visibility,
    IReadOnlyList<PriceListCategoryDto> Categories,
    IReadOnlyList<PriceListExclusionDto> Exclusions,
    bool ShowPrices,
    int OverrideCount,
    IReadOnlyList<string> Channels,
    int DistributorCount,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Todas las listas del tenant por nombre, sin paginar: un tenant tiene un puñado.</summary>
public sealed record PriceListsResponse(IReadOnlyList<PriceListSummaryDto> Items);

/// <summary>
/// Un precio fijo con el base al lado: la tabla pinta «base → lista» sin otra consulta. <c>BasePrice</c> es <c>null</c> si la
/// variante ya no tiene precio en esa moneda; entonces el precio fijo no aplica (D-2) y la pantalla lo marca.
/// </summary>
public sealed record PriceListOverrideDto(
    Guid VariantId, Guid ProductId, string Code, string Name, string Currency, decimal? BasePrice, decimal Amount);

public sealed record PriceListOverridesPageDto(
    IReadOnlyList<PriceListOverrideDto> Items, int Page, int PageSize, int Total, long Version);

/// <summary>Lo que escribió el lote, en el orden recibido, con la versión nueva para el siguiente <c>If-Match</c>.</summary>
public sealed record PriceListOverridesWriteDto(long Version, IReadOnlyList<PriceListOverrideDto> Rows);

public sealed record ChannelPriceListRefDto(Guid Id, string Name);

/// <summary>Un canal con su lista; <c>PriceList</c> <c>null</c> es «precio base».</summary>
public sealed record ChannelPriceDto(string Channel, ChannelPriceListRefDto? PriceList);

/// <summary>
/// «Precios por canal»: los tres canales siempre, en orden fijo e incluso sin lista (precedente <c>ReportStatusSliceDto</c>):
/// un canal que faltara obligaría a la pantalla a conocer el enum para dibujarlo.
/// </summary>
public sealed record ChannelPricesDto(IReadOnlyList<ChannelPriceDto> Channels);

/// <summary>Una exclusión como llega en el cuerpo. <c>Kind</c> va como texto para que un valor raro sea un 422 con el campo.</summary>
public sealed record PriceListExclusionInput(string Kind, Guid Id);

/// <summary>Lo que comparten alta y edición. Los comandos son planos para que <c>errors</c> diga <c>CategoryIds[1]</c>.</summary>
public interface IPriceListTermsCommand
{
    Guid TenantId { get; }

    string Name { get; }

    decimal AdjustmentPercent { get; }

    string Visibility { get; }

    IReadOnlyList<Guid> CategoryIds { get; }

    IReadOnlyList<PriceListExclusionInput>? Exclusions { get; }
}

internal sealed class PriceListWriteRules : AbstractValidator<IPriceListTermsCommand>
{
    // numeric(5,2) con signo: de -100 a 999.99, a lo sumo dos decimales. Los ceros a la derecha no cuentan.
    private const int AdjustmentPrecision = 5;

    private const int AdjustmentScale = 2;

    public PriceListWriteRules()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("The price list needs a name.")
            .Must(name => name is null || name.Trim().Length <= PriceList.NameMaxLength)
            .WithMessage($"The price list name has at most {PriceList.NameMaxLength} characters.");
        RuleFor(command => command.AdjustmentPercent)
            .InclusiveBetween(PriceList.MinAdjustment, PriceList.MaxAdjustment)
            .PrecisionScale(AdjustmentPrecision, AdjustmentScale, ignoreTrailingZeros: true);
        RuleFor(command => command.Visibility)
            .Must(visibility => PriceListTerms.TryParseVisibility(visibility, out _))
            .WithMessage("Visibility must be AllActive or Categories.");

        RuleFor(command => command.Exclusions).Custom((exclusions, context) =>
        {
            if (exclusions is null)
            {
                return;
            }

            var seen = new HashSet<(PriceListExclusionKind, Guid)>();
            var categories = 0;
            var products = 0;
            for (var index = 0; index < exclusions.Count; index++)
            {
                var item = exclusions[index];
                if (item is null || !PriceListTerms.TryParseKind(item.Kind, out var kind) || item.Id == Guid.Empty
                    || !seen.Add((kind, item.Id)))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Exclusions[{index}]", "The kind is not Category or Product, the id is empty, or it is already in the list."));
                    continue;
                }

                if (kind == PriceListExclusionKind.Category)
                {
                    categories++;
                }
                else
                {
                    products++;
                }
            }

            if (categories > PriceList.MaxHiddenCategories || products > PriceList.MaxHiddenProducts)
            {
                context.AddFailure(new ValidationFailure(
                    "Exclusions",
                    $"A price list hides at most {PriceList.MaxHiddenCategories} categories and {PriceList.MaxHiddenProducts} products."));
            }
        });

        // Con AllActive las categorías se ignoran y se guarda la lista vacía.
        When(command => IsCategories(command.Visibility), () =>
        {
            RuleFor(command => command.CategoryIds)
                .Must(ids => ids is { Count: > 0 })
                .WithMessage("Choose at least one category.")
                .Must(ids => ids is null || ids.Count <= PriceList.MaxCategories)
                .WithMessage($"A price list can have at most {PriceList.MaxCategories} categories.");
            RuleFor(command => command.CategoryIds).Custom((ids, context) =>
            {
                if (ids is null)
                {
                    return;
                }

                var seen = new HashSet<Guid>();
                for (var index = 0; index < ids.Count; index++)
                {
                    if (ids[index] == Guid.Empty || !seen.Add(ids[index]))
                    {
                        context.AddFailure(new ValidationFailure(
                            $"CategoryIds[{index}]", "The category is empty or already in the list."));
                    }
                }
            });
        });
    }

    private static bool IsCategories(string? visibility) =>
        PriceListTerms.TryParseVisibility(visibility, out var parsed) && parsed == PriceListVisibility.Categories;
}

/// <summary>Lo que los handlers hacen igual con los términos ya validados y con las respuestas.</summary>
internal static class PriceListTerms
{
    public static readonly string[] WrittenFields =
        ["name", "adjustmentPercent", "visibility", "categoryIds", "showPrices", "exclusions"];

    /// <summary>Por nombre, sin importar mayúsculas; no acepta números, que <c>Enum.TryParse</c> sí aceptaría.</summary>
    public static bool TryParseVisibility(string? value, out PriceListVisibility visibility) =>
        TryParseName(value, out visibility);

    public static bool TryParseKind(string? value, out PriceListExclusionKind kind) => TryParseName(value, out kind);

    public static bool TryParseChannel(string? value, out PriceChannel channel) => TryParseName(value, out channel);

    public static PriceListVisibility VisibilityOf(IPriceListTermsCommand command) =>
        TryParseVisibility(command.Visibility, out var visibility)
            ? visibility
            : throw new InvalidOperationException($"Visibility '{command.Visibility}' passed validation but is not known.");

    /// <summary>Vacía con <c>AllActive</c>, la del cuerpo con <c>Categories</c>.</summary>
    public static IReadOnlyList<Guid> CategoryIdsOf(IPriceListTermsCommand command) =>
        VisibilityOf(command) == PriceListVisibility.AllActive ? [] : command.CategoryIds;

    public static IReadOnlyList<PriceListExclusion> ExclusionsOf(IPriceListTermsCommand command) =>
        (command.Exclusions ?? [])
            .Select(item => TryParseKind(item.Kind, out var kind)
                ? new PriceListExclusion(kind, item.Id)
                : throw new InvalidOperationException($"Exclusion kind '{item.Kind}' passed validation but is not known."))
            .ToList();

    public static ResourceNotFoundException NotFound(Guid id) =>
        new("catalog.price_list.not_found", $"Price list '{id}' was not found.");

    public static ResourceConflictException NameTaken() =>
        new("catalog.price_list.name_taken", "Another price list already has that name.");

    public static ResourceConflictException InUse() =>
        new("catalog.price_list.in_use", "A channel or a distributor uses this price list.");

    public static void EnsureVersion(PriceList list, long expected)
    {
        if (list.Version != expected)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The price list changed after it was loaded.");
        }
    }

    /// <summary>
    /// Las categorías del cuerpo tienen que ser del tenant: una ajena o borrada es 422 <c>category_not_found</c>. Las
    /// exclusiones también, con el índice del cuerpo en <c>errors.Exclusions[i]</c>. Devuelve el árbol del tenant para la
    /// respuesta.
    /// </summary>
    public static async Task<PriceListCatalogNames> RequireReferencesAsync(
        IProductCategoryRepository categories,
        IProductRepository products,
        IPriceListTermsCommand command,
        CancellationToken cancellationToken)
    {
        var names = await PriceListCatalogNames.LoadAsync(
            categories,
            products,
            command.TenantId,
            ExclusionsOf(command).Where(item => item.Kind == PriceListExclusionKind.Product).Select(item => item.Id).ToList(),
            cancellationToken);

        if (CategoryIdsOf(command).Any(id => !names.Categories.ContainsKey(id)))
        {
            throw new CatalogDomainException(
                "catalog.price_list.category_not_found", "One of the categories does not exist in the catalog.");
        }

        var parsed = ExclusionsOf(command);
        var failures = new List<ValidationFailure>();
        for (var index = 0; index < parsed.Count; index++)
        {
            var found = parsed[index].Kind == PriceListExclusionKind.Category
                ? names.Categories.ContainsKey(parsed[index].Id)
                : names.Products.ContainsKey(parsed[index].Id);
            if (!found)
            {
                failures.Add(new ValidationFailure($"Exclusions[{index}]", "The category or product does not exist in the catalog."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return names;
    }

    public static IReadOnlyList<string> ChannelsOf(PriceList list, IReadOnlyList<ChannelPriceList> assignments) =>
        assignments
            .Where(assignment => assignment.PriceListId == list.Id)
            .Select(assignment => assignment.Channel)
            .Order()
            .Select(channel => channel.ToString())
            .ToList();

    public static PriceListDetailDto ToDetail(
        PriceList list,
        PriceListCatalogNames names,
        int overrideCount,
        IReadOnlyList<string> channels,
        int distributorCount) =>
        new(
            list.Id.Value,
            list.Name,
            list.AdjustmentPercent,
            list.Visibility.ToString(),
            names.CategoriesOf(list),
            names.ExclusionsOf(list),
            list.ShowPrices,
            overrideCount,
            channels,
            distributorCount,
            list.Version,
            list.CreatedAt,
            list.UpdatedAt);

    private static bool TryParseName<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                parsed = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>Los nombres de categorías y productos que una lista nombra, leídos una vez por request.</summary>
internal sealed class PriceListCatalogNames
{
    private PriceListCatalogNames(
        IReadOnlyDictionary<Guid, ProductCategoryNode> categories, IReadOnlyDictionary<Guid, ProductBasics> products)
    {
        Categories = categories;
        Products = products;
    }

    public IReadOnlyDictionary<Guid, ProductCategoryNode> Categories { get; }

    public IReadOnlyDictionary<Guid, ProductBasics> Products { get; }

    public static async Task<PriceListCatalogNames> LoadAsync(
        IProductCategoryRepository categories,
        IProductRepository products,
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken)
    {
        var tree = await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken);
        var found = productIds.Count == 0
            ? []
            : await products.FindBasicsByIdsAsync(
                tenantId, productIds.Distinct().Select(id => new ProductId(id)).ToList(), cancellationToken);
        return new PriceListCatalogNames(
            tree.ToDictionary(node => node.Id.Value),
            found.ToDictionary(product => product.Id.Value));
    }

    public IReadOnlyList<PriceListCategoryDto> CategoriesOf(PriceList list) =>
        list.CategoryIds
            .Where(Categories.ContainsKey)
            .Select(id => new PriceListCategoryDto(id, Categories[id].Name))
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(category => category.Id)
            .ToList();

    /// <summary>Categorías primero y luego productos, cada grupo por nombre. Un destino que ya no existe no viaja.</summary>
    public IReadOnlyList<PriceListExclusionDto> ExclusionsOf(PriceList list)
    {
        var hiddenCategories = list.ExcludedCategoryIds
            .Where(Categories.ContainsKey)
            .Select(id => new PriceListExclusionDto(nameof(PriceListExclusionKind.Category), id, Categories[id].Name, null, null));
        var hiddenProducts = list.ExcludedProductIds
            .Where(Products.ContainsKey)
            .Select(id => Products[id])
            .Select(product => new PriceListExclusionDto(
                nameof(PriceListExclusionKind.Product),
                product.Id.Value,
                product.Name,
                product.SingleVariantCode,
                product.CategoryId is { } categoryId && Categories.TryGetValue(categoryId.Value, out var category)
                    ? category.Name
                    : null));

        return
        [
            .. hiddenCategories.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id),
            .. hiddenProducts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id),
        ];
    }
}
