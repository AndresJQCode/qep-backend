namespace Modules.Purchases.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Dos y no más: leer incluye la lista, el detalle y el PDF;
// gestionar incluye crear, editar, borrar, enviar, confirmar, recibir y cancelar.
public static class PurchasesPermissions
{
    public const string OrderRead = "purchases.order.read";

    public const string OrderManage = "purchases.order.manage";
}
