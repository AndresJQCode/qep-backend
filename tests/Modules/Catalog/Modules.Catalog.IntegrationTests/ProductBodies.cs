using Modules.Catalog.Application;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Cuerpos crudos del POST y el PUT de producto desde las variantes (Tarea 5): el código y el
/// precio viajan en <c>variants[]</c>, la imagen en <c>gallery[]</c> y el PUT lleva
/// <c>expectedVersion</c>. Un solo lugar para armarlos: la lección de CLAUDE.md («al agregar un
/// campo requerido, barre las pruebas que arman el cuerpo a mano») es que cada copia suelta es una
/// prueba que se rompe por otra causa.
/// </summary>
internal static class ProductBodies
{
    public static object UsdPricing(decimal usd = 10m) =>
        new { prices = new Dictionary<string, decimal> { ["USD"] = usd } };

    /// <summary>Un producto sin opciones: una sola variante, la por defecto.</summary>
    public static object Create(
        string name,
        string code,
        object? pricing = null,
        string? description = null,
        Guid? taxRateId = null,
        Guid? imageFileId = null,
        string? barcode = null,
        Guid? categoryId = null) =>
        new
        {
            name,
            description,
            taxRateId,
            categoryId,
            gallery = imageFileId is { } image ? new[] { image } : [],
            variants = new[] { Variant(null, code, pricing ?? UsdPricing(), barcode) }
        };

    /// <summary>
    /// El PUT de un producto sin opciones: su única variante, por id, con la versión que se leyó.
    /// </summary>
    public static object Update(
        ProductResponse current,
        string name,
        string code,
        object? pricing = null,
        string? description = null,
        Guid? taxRateId = null,
        Guid? imageFileId = null,
        Guid? categoryId = null) =>
        Update(current.Version, Assert.Single(current.Variants).Id, name, code, pricing, description, taxRateId, imageFileId, categoryId);

    public static object Update(
        long expectedVersion,
        Guid variantId,
        string name,
        string code,
        object? pricing = null,
        string? description = null,
        Guid? taxRateId = null,
        Guid? imageFileId = null,
        Guid? categoryId = null) =>
        new
        {
            expectedVersion,
            name,
            description,
            taxRateId,
            categoryId,
            gallery = imageFileId is { } image ? new[] { image } : [],
            variants = new[] { Variant(variantId, code, pricing ?? UsdPricing(), barcode: null) }
        };

    public static object Variant(Guid? id, string code, object? pricing, string? barcode = null) =>
        new { id, code, barcode, pricing };

    /// <summary>La única variante de un producto sin opciones.</summary>
    public static ProductVariantResponse Only(this ProductResponse product) => Assert.Single(product.Variants);
}
