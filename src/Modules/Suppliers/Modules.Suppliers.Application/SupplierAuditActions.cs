namespace Modules.Suppliers.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec §5); <c>Exported</c> la usa la exportación (SUP-07).</summary>
public static class SupplierAuditActions
{
    public const string Created = "suppliers.supplier.created";

    public const string Updated = "suppliers.supplier.updated";

    public const string Activated = "suppliers.supplier.activated";

    public const string Deactivated = "suppliers.supplier.deactivated";

    public const string Exported = "suppliers.supplier.exported";

    /// <summary>Spec 2026-11-17 (mayorista), D-24: el proveedor quedó vinculado a un mayorista al aceptar su invitación.</summary>
    public const string Linked = "suppliers.supplier.linked";
}
