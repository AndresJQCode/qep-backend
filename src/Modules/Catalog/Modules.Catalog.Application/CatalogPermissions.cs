namespace Modules.Catalog.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions).
//
// Los de tasas de impuesto —catalog.tax_rate.read y .manage— estaban acá, registrados en roles
// y publicados en /authorization/catalog, sin una sola línea que los consumiera. Las tasas son
// de CAT-03, declarado fuera de alcance en el spec de CAT-02. Un permiso publicado antes que su
// funcionalidad le dice al frontend que existe algo que no existe, y le cuelga a tenancy.owner
// un permiso de gestión sobre nada. Se quitaron en la corrección de la revisión de CAT-02 y
// vuelven con CAT-03, junto a su implementación. Van separados de los de producto porque cambiar
// una tasa mueve los totales de toda cotización, y cargar un producto no.
public static class CatalogPermissions
{
    public const string ProductRead = "catalog.product.read";
    public const string ProductManage = "catalog.product.manage";
}
