namespace Modules.Wms.Application;

/// <summary>
/// Spec WMS 2026-10-31 §11.5 y RF-100, más el del motor de tareas (spec 2026-11-26 §6.11): diecisiete permisos
/// <c>wms.&lt;recurso&gt;.&lt;acción&gt;</c>, todos con
/// <c>RequiredModules: [wms]</c> (con el módulo apagado la máscara de entitlements los quita). Se registran todos desde el primer
/// corte aunque sus casos de uso lleguen después: <c>CompositionRootTests</c> exige definición y política por cada constante, y
/// sin la política <c>RequireAuthorization</c> no resuelve y el síntoma es 500, no 403.
/// </summary>
public static class WmsPermissions
{
    /// <summary>Crear y editar bodegas y ubicaciones.</summary>
    public const string WarehouseManage = "wms.warehouse.manage";

    /// <summary>Crear y editar dueños de inventario.</summary>
    public const string OwnerManage = "wms.owner.manage";

    /// <summary>Crear y editar perfiles de variante, unidades de medida y conversiones.</summary>
    public const string ProfileManage = "wms.profile.manage";

    /// <summary>Ver bodegas, ubicaciones, documentos y tareas.</summary>
    public const string DocumentRead = "wms.document.read";

    /// <summary>Ver existencias.</summary>
    public const string StockRead = "wms.stock.read";

    /// <summary>Exportar existencias.</summary>
    public const string StockExport = "wms.stock.export";

    /// <summary>Proponer un ajuste de inventario.</summary>
    public const string AdjustmentPropose = "wms.adjustment.propose";

    /// <summary>Aprobar un ajuste de inventario.</summary>
    public const string AdjustmentApprove = "wms.adjustment.approve";

    /// <summary>Crear y gestionar órdenes de compra.</summary>
    public const string PurchaseOrderManage = "wms.purchase_order.manage";

    /// <summary>Crear y gestionar traslados.</summary>
    public const string TransferManage = "wms.transfer.manage";

    /// <summary>Despachar un traslado.</summary>
    public const string TransferDispatch = "wms.transfer.dispatch";

    /// <summary>Recibir mercancía.</summary>
    public const string ReceiptPerform = "wms.receipt.perform";

    /// <summary>Ubicar mercancía recibida.</summary>
    public const string PutawayPerform = "wms.putaway.perform";

    /// <summary>Resolver discrepancias de recepción.</summary>
    public const string DiscrepancyResolve = "wms.discrepancy.resolve";

    /// <summary>Contar inventario.</summary>
    public const string CountPerform = "wms.count.perform";

    /// <summary>Aprobar un conteo.</summary>
    public const string CountApprove = "wms.count.approve";

    /// <summary>Supervisar tareas: crear, asignar, priorizar, cancelar.</summary>
    public const string TaskManage = "wms.task.manage";
}
