namespace Modules.Suppliers.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec §5); <c>Exported</c> la usa la exportación (SUP-07).</summary>
public static class SupplierAuditActions
{
    public const string Created = "suppliers.supplier.created";

    public const string Updated = "suppliers.supplier.updated";

    public const string Activated = "suppliers.supplier.activated";

    public const string Deactivated = "suppliers.supplier.deactivated";

    public const string Exported = "suppliers.supplier.exported";
}
