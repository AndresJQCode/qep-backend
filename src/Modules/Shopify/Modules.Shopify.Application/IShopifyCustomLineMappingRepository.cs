using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-13: las asociaciones de líneas personalizadas (<c>shopify.custom_line_mappings</c>). Con tracking; lo
/// agregado o quitado commitea con <see cref="IShopifyUnitOfWork"/>, que traduce el índice único a
/// <c>shopify.custom_line.already_mapped</c>. Todo método recibe el tenant y la conexión: una asociación nunca aplica fuera de
/// su tienda.</summary>
public interface IShopifyCustomLineMappingRepository
{
    /// <summary>Claves ordinales = título recortado; sólo filas de <paramref name="tenantId"/> y
    /// <paramref name="connectionId"/>. Una consulta para todos los títulos.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindVariantIdsByTitlesAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<string> titles, CancellationToken cancellationToken);

    Task<IReadOnlyList<ShopifyCustomLineMapping>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<ShopifyCustomLineMapping?> FindAsync(Guid tenantId, Guid connectionId, Guid id, CancellationToken cancellationToken);

    void Add(ShopifyCustomLineMapping mapping);

    void Remove(ShopifyCustomLineMapping mapping);
}
