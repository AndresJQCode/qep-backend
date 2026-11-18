using BuildingBlocks.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Las respuestas de «no se puede» de los handlers, por tipo y no por status (el mapeo vive en <c>ApiExceptionHandler</c>).
/// </summary>
internal static class PurchasesConflicts
{
    /// <summary>Código existente de Suppliers (D-3): Compras no inventa uno propio para «ese proveedor no está».</summary>
    public const string SupplierNotFoundCode = "suppliers.supplier.not_found";

    // Las búsquedas van acotadas al tenant del llamador: «no encontrada» es «no está entre tus órdenes».
    public static ResourceNotFoundException NotFound(Guid id) =>
        new(PurchasesErrorCodes.NotFound, $"Purchase order '{id}' was not found.");

    public static ResourceNotFoundException SupplierNotFound(Guid id) =>
        new(SupplierNotFoundCode, $"Supplier '{id}' was not found.");

    public static ResourceConflictException NotDraft() =>
        new(PurchasesErrorCodes.NotDraft, "Only a draft purchase order can be edited or deleted.");

    public static ResourceConflictException InvalidTransition() =>
        new(PurchasesErrorCodes.InvalidTransition, "The purchase order status does not allow this action.");

    public static RequestConcurrencyException Stale() =>
        new("concurrency.conflict", "The purchase order changed after it was loaded.");

    /// <summary>
    /// D-1: el 409 sale de acá, antes de llamar al dominio. Editar y borrar responden <c>not_draft</c>; el resto,
    /// <c>invalid_transition</c>. El dominio repite la guarda como última red.
    /// </summary>
    public static void EnsureAllowed(PurchaseOrder order, PurchaseOrderAction action)
    {
        if (order.AllowedActions().Contains(action))
        {
            return;
        }

        throw action is PurchaseOrderAction.Edit or PurchaseOrderAction.Delete ? NotDraft() : InvalidTransition();
    }

    public static void EnsureVersion(PurchaseOrder order, long expected)
    {
        if (order.Version != expected)
        {
            throw Stale();
        }
    }
}
