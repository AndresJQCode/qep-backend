using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Siigo.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: las variantes del catálogo para la tabla de vínculos, la cobertura y «Vincular por código». El vínculo es
/// por variante (en QEP el código vive en <c>catalog.product_variants</c> y cada variante es un producto distinto en Siigo). La página sale
/// de una proyección sobre variantes y productos, sin cargar el agregado (un producto llega a 2.048 variantes); sólo las filas de la página
/// pasan por <see cref="IProductRepository.FindVariantsByIdsAsync"/> y la biblioteca de opciones para armar el nombre visible con
/// <see cref="CatalogVariantReads.DisplayNameOf(string, IReadOnlyList{Guid}, IReadOnlyDictionary{Guid, string})"/>, el mismo que usan las
/// líneas de una cotización. «Activa» es variante <b>y</b> producto activos, como lo que se puede vender.
/// </summary>
internal sealed class SiigoVariantCatalog(CatalogDbContext catalog, IProductRepository products, IOptionDefinitionRepository options)
    : ISiigoVariantCatalog
{
    public async Task<SiigoPage<SiigoVariantRef>> SearchAsync(
        Guid tenantId,
        string? search,
        IReadOnlyCollection<Guid>? onlyVariantIds,
        IReadOnlyCollection<Guid>? excludeVariantIds,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // «Sólo esas» con ninguna es ninguna fila, sin ir a la base.
        if (onlyVariantIds is { Count: 0 })
        {
            return new SiigoPage<SiigoVariantRef>([], (skip / Math.Max(take, 1)) + 1, take, 0);
        }

        var query = Rows(tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(row => EF.Functions.ILike(row.Code, pattern) || EF.Functions.ILike(row.ProductName, pattern));
        }

        if (onlyVariantIds is not null)
        {
            var only = onlyVariantIds.Select(id => new VariantId(id)).ToArray();
            query = query.Where(row => only.Contains(row.Id));
        }

        if (excludeVariantIds is { Count: > 0 })
        {
            var excluded = excludeVariantIds.Select(id => new VariantId(id)).ToArray();
            query = query.Where(row => !excluded.Contains(row.Id));
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query
            .OrderBy(row => row.ProductName)
            .ThenBy(row => row.Code)
            .ThenBy(row => row.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var names = await DisplayNamesAsync(tenantId, page.Select(row => row.Id).ToArray(), cancellationToken);
        return new SiigoPage<SiigoVariantRef>(
            [.. page.Select(row => new SiigoVariantRef(row.Id.Value, names.GetValueOrDefault(row.Id.Value, row.ProductName), row.Code, row.IsActive))],
            (skip / Math.Max(take, 1)) + 1,
            take,
            total);
    }

    public async Task<int> CountActiveAsync(Guid tenantId, IReadOnlyCollection<Guid>? onlyVariantIds, CancellationToken cancellationToken)
    {
        var query = Rows(tenantId).Where(row => row.IsActive);
        if (onlyVariantIds is not null)
        {
            if (onlyVariantIds.Count == 0)
            {
                return 0;
            }

            var only = onlyVariantIds.Select(id => new VariantId(id)).ToArray();
            query = query.Where(row => only.Contains(row.Id));
        }

        return await query.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SiigoVariantRef>> ListAllActiveCodesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [
            .. (await Rows(tenantId).Where(row => row.IsActive).ToListAsync(cancellationToken))
                .Select(row => new SiigoVariantRef(row.Id.Value, row.ProductName, row.Code, row.IsActive)),
        ];

    // Variante y producto del tenant; los dos filtran por tenant_id: la unión no deja pasar una variante ajena.
    private IQueryable<VariantRow> Rows(Guid tenantId) =>
        from variant in catalog.ProductVariants.AsNoTracking()
        join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
        where variant.TenantId == tenantId && product.TenantId == tenantId
        select new VariantRow { Id = variant.Id, Code = variant.Code, ProductName = product.Name, IsActive = variant.IsActive && product.IsActive };

    private async Task<Dictionary<Guid, string>> DisplayNamesAsync(Guid tenantId, VariantId[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var found = await products.FindVariantsByIdsAsync(tenantId, ids, cancellationToken);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, cancellationToken);
        var wanted = ids.Select(id => id.Value).ToHashSet();
        return found
            .SelectMany(product => product.Variants
                .Where(variant => wanted.Contains(variant.Id.Value))
                .Select(variant => (variant.Id.Value, Name: CatalogVariantReads.DisplayNameOf(product, variant, valueNames))))
            .ToDictionary(pair => pair.Value, pair => pair.Name);
    }

    // % y _ son comodines de ILIKE: un código con guion bajo no debe casar con cualquier carácter.
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // Con inicializadores y no con constructor: EF compone los filtros siguientes sobre esta proyección sólo si puede mapear cada
    // miembro a su columna.
    private sealed class VariantRow
    {
        public VariantId Id { get; init; }

        public string Code { get; init; } = string.Empty;

        public string ProductName { get; init; } = string.Empty;

        public bool IsActive { get; init; }
    }
}
