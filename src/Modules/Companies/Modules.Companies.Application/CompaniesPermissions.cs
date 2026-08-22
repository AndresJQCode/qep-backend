namespace Modules.Companies.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions, CatalogPermissions).
//
// Dos y no mas. Un permiso publicado antes que su funcionalidad le dice al frontend que existe
// algo que no existe, y le cuelga a admin un permiso de gestion sobre nada: es lo que se
// quito en la correccion de la revision de CAT-02. Si el borrado duro llega alguna vez, llega en
// el mismo commit que su endpoint.
public static class CompaniesPermissions
{
    public const string CompanyRead = "companies.company.read";

    public const string CompanyManage = "companies.company.manage";
}
