using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>
/// Resuelve el <c>categoryId</c> que llega en un comando contra las categorías **del tenant del
/// producto**. Mismo molde que <see cref="ProductTaxRateResolver"/>: la foreign key
/// `catalog.products.category_id` sólo garantiza que la fila exista, no que sea del mismo tenant.
/// </summary>
internal static class ProductCategoryResolver
{
    public static async Task<ProductCategoryId?> ResolveAsync(
        IProductCategoryRepository repository,
        Guid tenantId,
        Guid? categoryId,
        CancellationToken cancellationToken)
    {
        if (categoryId is null)
        {
            return null;
        }

        var candidate = new ProductCategoryId(categoryId.Value);

        // El filtro de tenant es parte de la consulta. Inexistente y ajena dan el mismo código a
        // propósito —el mismo que la traducción de la FK—: distinguirlas confirmaría que el id
        // existe en otro tenant.
        var category = await repository.FindAsync(tenantId, candidate, cancellationToken);

        return category is null
            ? throw new CatalogDomainException(
                "catalog.product.category_not_found",
                "The category was not found in this tenant.")
            : candidate;
    }
}
