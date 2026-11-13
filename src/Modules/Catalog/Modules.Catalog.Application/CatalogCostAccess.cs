using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Quién ve y quién escribe el costo de una variante. El costo es dato comercial sensible, así que
/// va detrás de sus propios permisos y no detrás de <c>catalog.product.*</c>.
/// </summary>
internal static class CatalogCostAccess
{
    // Manage implies read: someone who may write a cost must be able to see what they are editing.
    public static bool CanRead(IExecutionContext executionContext) =>
        executionContext.HasPermission(CatalogPermissions.ProductCostRead)
        || executionContext.HasPermission(CatalogPermissions.ProductCostManage);

    /// <summary>
    /// Se llama antes del validador: quien no puede escribir costos no debe recibir ni siquiera el
    /// mapa de errores de un cuerpo que no le corresponde. Un <c>costs</c> nulo no escribe nada.
    /// </summary>
    public static void EnsureCanWrite(
        IExecutionContext executionContext,
        IEnumerable<ProductVariantRequest>? variants)
    {
        if (variants is null
            || !variants.Any(variant => variant?.Costs is not null)
            || executionContext.HasPermission(CatalogPermissions.ProductCostManage))
        {
            return;
        }

        throw new RequestForbiddenException(
            "catalog.product_cost.forbidden",
            "The subject cannot write product costs.");
    }
}
