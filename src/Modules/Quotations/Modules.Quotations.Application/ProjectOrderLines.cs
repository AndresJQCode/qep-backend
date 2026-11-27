using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El candado de las líneas de un pedido de proyecto (spec 2026-11-26 §6.6 y §12-C): lo vendido es el alcance, así que ni
/// la línea ni los regalos se cambian. Lo comparten el guardado, el cálculo previo y «agregar productos». El agregado
/// rechaza lo mismo en cada método de líneas; aquí sale antes de leer el catálogo o de publicar comprobantes.
/// </summary>
internal static class ProjectOrderLines
{
    /// <summary>
    /// Devuelve <c>false</c> si el pedido no es de proyecto. En uno de proyecto devuelve <c>true</c> cuando la edición deja
    /// la línea como está —sin líneas, o con exactamente la que hay: misma variante y cantidad, y precio ausente o igual
    /// al guardado— y no trae regalos; cualquier otra cosa es <c>order.project.items_locked</c>. Con <c>true</c>, quien
    /// llama no toca líneas, regalos, detal, piso de escala ni envío.
    /// </summary>
    public static bool KeepsTheLine(
        Order order, IReadOnlyList<OrderItemAddition> items, IReadOnlyList<OrderItemAddition>? gifts)
    {
        if (order.Kind != QuotationKind.Project)
        {
            return false;
        }

        if (gifts is { Count: > 0 })
        {
            throw Locked();
        }

        return items.Count == 0 || (items is [var wanted] && order.Items.Any(line => IsSameLine(line, wanted)))
            ? true
            : throw Locked();
    }

    /// <summary>A un pedido de proyecto no se le agregan productos.</summary>
    public static void EnsureAcceptsNewItems(Order order)
    {
        if (order.Kind == QuotationKind.Project)
        {
            throw Locked();
        }
    }

    private static bool IsSameLine(OrderItem line, OrderItemAddition wanted) =>
        wanted.VariantId == line.VariantId
        && wanted.Quantity == line.Quantity
        && (wanted.UnitPrice is null || wanted.UnitPrice == line.UnitPrice);

    private static QuotationsDomainException Locked() =>
        new(ProjectErrors.ItemsLocked, ProjectErrors.ItemsLockedMessage);
}
