using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

/// <summary>Spec 2026-11-22: listas de precios, precios fijos y asignación de canales.</summary>
internal sealed class PriceListRepository(CatalogDbContext dbContext) : IPriceListRepository
{
    private const string LikeEscapeCharacter = "\\";

    public Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken) =>
        dbContext.PriceLists.FirstOrDefaultAsync(list => list.TenantId == tenantId && list.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.PriceLists.AsNoTracking().Where(list => list.TenantId == tenantId).ToListAsync(cancellationToken);

    // La minúscula invariante de .NET y lower() de Postgres coinciden en todo texto que no sea exótico; donde no, el índice
    // sigue siendo la red y la unidad de trabajo da el mismo 409.
    public Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var lists = dbContext.PriceLists.AsNoTracking()
            .Where(list => list.TenantId == tenantId
                && EF.Property<string>(list, CatalogDbContext.PriceListNormalizedName) == normalized);
        if (except is { } excluded)
        {
            lists = lists.Where(list => list.Id != excluded);
        }

        return lists.AnyAsync(cancellationToken);
    }

    public void Add(PriceList list) => dbContext.PriceLists.Add(list);

    public void Remove(PriceList list) => dbContext.PriceLists.Remove(list);

    public async Task<IReadOnlyDictionary<PriceListId, int>> CountOverridesAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.PriceListOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .GroupBy(item => item.PriceListId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Count, cancellationToken);

    public async Task<(IReadOnlyList<PriceListOverrideRow> Items, int Total)> PageOverridesAsync(
        Guid tenantId, PriceListId id, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows =
            from item in dbContext.PriceListOverrides.AsNoTracking()
            join variant in dbContext.ProductVariants.AsNoTracking() on item.VariantId equals variant.Id
            join product in dbContext.Products.AsNoTracking() on variant.ProductId equals product.Id
            where item.TenantId == tenantId && item.PriceListId == id && product.TenantId == tenantId
            select new { item.VariantId, item.Currency, item.Amount, ProductName = product.Name, ProductId = product.Id, variant.Code, variant.Position };

        if (search is not null)
        {
            var pattern = $"%{EscapeLikeWildcards(search)}%";
            rows = rows.Where(row => EF.Functions.ILike(row.ProductName, pattern, LikeEscapeCharacter)
                || EF.Functions.ILike(row.Code, pattern, LikeEscapeCharacter));
        }

        var total = await rows.CountAsync(cancellationToken);
        var found = await rows
            .OrderBy(row => row.ProductName)
            .ThenBy(row => row.ProductId)
            .ThenBy(row => row.Position)
            .ThenBy(row => row.Currency)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new { row.VariantId, row.Currency, row.Amount })
            .ToListAsync(cancellationToken);

        var variants = await FindVariantsAsync(
            tenantId, found.Select(row => row.VariantId.Value).Distinct().ToList(), cancellationToken);
        var items = found
            .Where(row => variants.ContainsKey(row.VariantId.Value))
            .Select(row =>
            {
                var variant = variants[row.VariantId.Value];
                return new PriceListOverrideRow(
                    variant.VariantId,
                    variant.ProductId,
                    variant.Code,
                    variant.Name,
                    row.Currency,
                    variant.BasePrices.TryGetValue(row.Currency, out var basePrice) ? basePrice : null,
                    row.Amount);
            })
            .ToList();
        return (items, total);
    }

    public async Task<IReadOnlyList<PriceListOverride>> FindOverridesForUpdateAsync(
        Guid tenantId, PriceListId id, IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var wanted = variantIds.ToArray();
        return await dbContext.PriceListOverrides
            .Where(item => item.TenantId == tenantId && item.PriceListId == id && wanted.Contains(item.VariantId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<(Guid VariantId, string Currency), decimal>> FindOverrideAmountsAsync(
        Guid tenantId, PriceListId id, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<(Guid, string), decimal>();
        }

        var wanted = variantIds.Distinct().Select(value => new VariantId(value)).ToArray();
        var found = await dbContext.PriceListOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PriceListId == id && wanted.Contains(item.VariantId))
            .Select(item => new { item.VariantId, item.Currency, item.Amount })
            .ToListAsync(cancellationToken);
        return found.ToDictionary(item => (item.VariantId.Value, item.Currency), item => item.Amount);
    }

    public async Task<IReadOnlyDictionary<Guid, PriceListVariantRef>> FindVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, PriceListVariantRef>();
        }

        var wanted = variantIds.Distinct().Select(value => new VariantId(value)).ToArray();
        var products = await dbContext.Products.AsNoTracking()
            .Include(product => product.Variants)
            .Where(product => product.TenantId == tenantId && product.Variants.Any(variant => wanted.Contains(variant.Id)))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Los nombres de los valores de opción de esas variantes, en una consulta: el nombre visible se arma con VariantDisplayName.
        var optionIds = products.SelectMany(product => product.OptionIds).Distinct().Select(value => new OptionDefinitionId(value)).ToArray();
        var valueNames = optionIds.Length == 0
            ? new Dictionary<Guid, string>()
            : (await dbContext.OptionDefinitions.AsNoTracking()
                    .Include(option => option.Values)
                    .Where(option => option.TenantId == tenantId && optionIds.Contains(option.Id))
                    .ToListAsync(cancellationToken))
                .SelectMany(option => option.Values)
                .ToDictionary(value => value.Id, value => value.Value);

        var wantedSet = wanted.ToHashSet();
        return products
            .SelectMany(product => product.Variants
                .Where(variant => wantedSet.Contains(variant.Id))
                .Select(variant => new PriceListVariantRef(
                    variant.Id.Value,
                    product.Id.Value,
                    variant.Code,
                    VariantDisplayName.For(
                        product.Name,
                        variant.OptionValueIds.Select(valueId => valueNames.GetValueOrDefault(valueId, string.Empty)).ToList()),
                    variant.Prices.ToDictionary(price => price.Currency, price => price.Amount, StringComparer.Ordinal))))
            .ToDictionary(variant => variant.VariantId);
    }

    public void AddOverride(PriceListOverride item) => dbContext.PriceListOverrides.Add(item);

    public void RemoveOverride(PriceListOverride item) => dbContext.PriceListOverrides.Remove(item);

    public async Task<IReadOnlyList<ChannelPriceList>> ListChannelsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.ChannelPriceLists.Where(assignment => assignment.TenantId == tenantId).ToListAsync(cancellationToken);

    public void AddChannel(ChannelPriceList assignment) => dbContext.ChannelPriceLists.Add(assignment);

    public void RemoveChannel(ChannelPriceList assignment) => dbContext.ChannelPriceLists.Remove(assignment);

    // La barra va primero: escaparla después convertiría en literal la barra que agregan los otros dos (como ProductRepository).
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
