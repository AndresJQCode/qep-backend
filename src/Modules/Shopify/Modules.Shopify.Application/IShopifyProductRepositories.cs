using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-17 §3: los vínculos de variantes (<c>shopify.product_links</c>). Con tracking; lo agregado o quitado
/// commitea con <see cref="IShopifyUnitOfWork"/>, que traduce los dos índices únicos a <c>concurrency.conflict</c> (el perdedor
/// relee).</summary>
public interface IShopifyProductLinkRepository
{
    /// <summary>Los vínculos de un producto de Shopify en esa conexión.</summary>
    Task<IReadOnlyList<ShopifyProductLink>> FindByShopifyProductAsync(
        Guid connectionId, long shopifyProductId, CancellationToken cancellationToken);

    /// <summary>Clave = id de la variante de Shopify. Una consulta para todas; sólo filas del tenant y la conexión. Sin
    /// tracking: es la lectura del resolver de líneas.</summary>
    Task<IReadOnlyDictionary<long, ShopifyProductLink>> FindByShopifyVariantsAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<long> shopifyVariantIds, CancellationToken cancellationToken);

    /// <summary>Los vínculos de un producto de QEP en esa conexión.</summary>
    Task<IReadOnlyList<ShopifyProductLink>> FindByProductAsync(
        Guid tenantId, Guid connectionId, Guid productId, CancellationToken cancellationToken);

    /// <summary>Los vínculos de un producto de QEP en cualquier tienda del tenant (desvincular, §7).</summary>
    Task<IReadOnlyList<ShopifyProductLink>> FindByProductInTenantAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    /// <summary>Los vínculos de esas variantes de QEP en esa conexión.</summary>
    Task<IReadOnlyList<ShopifyProductLink>> FindByVariantsAsync(
        Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Los productos de QEP con algún vínculo <c>Imported</c> en esa conexión (P9).</summary>
    Task<IReadOnlySet<Guid>> ListImportedProductIdsAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todos los ids de variante de Shopify vinculados en esa conexión (modo <c>Missing</c> y cobertura).</summary>
    Task<IReadOnlySet<long>> ListLinkedShopifyVariantIdsAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-17 (onboarding) §7: los productos de QEP distintos con algún vínculo en esa conexión, para la
    /// cabecera del asistente («productos sincronizados»).</summary>
    Task<int> CountLinkedProductsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(ShopifyProductLink link);

    void Remove(ShopifyProductLink link);
}

/// <summary>Spec 2026-11-17 §5.2: las importaciones (<c>shopify.product_imports</c>) y sus issues. El 409 de una segunda
/// importación viva lo da el índice único al guardar, no una consulta previa.</summary>
public interface IShopifyProductImportRepository
{
    /// <summary>Con tracking.</summary>
    Task<ShopifyProductImport?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>La más reciente de la conexión, sin tracking.</summary>
    Task<ShopifyProductImport?> FindLatestAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    void Add(ShopifyProductImport import);

    void AddIssue(ShopifyProductImportIssue issue);

    /// <summary>Las primeras <paramref name="limit"/> en orden de creación, sin tracking.</summary>
    Task<IReadOnlyList<ShopifyProductImportIssue>> ListIssuesAsync(Guid importId, int limit, CancellationToken cancellationToken);
}
