namespace Modules.Tenancy.Application;

public static class TenancyPermissions
{
    public const string SettingsRead = "tenancy.settings.read";
    public const string SettingsUpdate = "tenancy.settings.update";

    // `advisorship.*` y no `tenancy.membership.*`: la membresía de este producto es una
    // asesoría, y el permiso nombra la capacidad de negocio, no la tabla que la guarda. El
    // rename acompaña al del catálogo de roles (`admin` / `advisor` / `billing`).
    //
    // Sin prefijo de módulo, a diferencia de `catalog.product.read`, porque la asesoría no es
    // propiedad de Tenancy: es transversal al tenant, igual que el rol.
    //
    // No confundir con dos familias vecinas que NO se renombraron, porque están persistidas:
    // la acción de auditoría `tenancy.membership.invited` y el evento de outbox
    // `tenancy.membership-invited.v1`. Los permisos se derivan del rol en cada request y no
    // tocan un solo registro; esas dos sí, y renombrarlas requiere migración propia.
    public const string AdvisorshipInvite = "advisorship.invite";
    public const string AdvisorshipRead = "advisorship.read";
    public const string AdvisorshipManage = "advisorship.manage";
}
