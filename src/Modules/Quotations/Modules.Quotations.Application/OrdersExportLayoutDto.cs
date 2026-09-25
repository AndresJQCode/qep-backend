using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una columna del layout efectivo (spec 2026-09-24, "Application y API"). <c>DefaultHeader</c> y
/// <c>DefaultPosition</c> viajan por columna (regla BFF del repo): la pantalla los necesita como
/// placeholder del input, para "restaurar" una sola y para "restaurar todo" sin conocer el
/// catálogo. <c>DefaultPosition</c> es 1-based (1..35), como la columna # de la tabla del spec: es
/// la posición que la pantalla muestra. Una llave repetida (ajuste 2026-09-25) trae el mismo
/// <c>DefaultHeader</c> y <c>DefaultPosition</c> en cada entrada. Nulos en una fija, que no tiene
/// defecto; <c>Value</c> nulo en una del catálogo.
/// </summary>
public sealed record OrdersExportColumnDto(
    string Kind,
    string? Key,
    string? DefaultHeader,
    int? DefaultPosition,
    string Header,
    string? Value,
    bool Visible);

/// <summary>El layout efectivo (D8), entero y en orden (D7). <c>Version</c> es la de la fila, o
/// 1 si no hay fila (D9).</summary>
public sealed record OrdersExportLayoutDto(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnDto> Columns,
    long Version);

public static class OrdersExportLayoutMappings
{
    public static OrdersExportLayoutDto ToDto(OrdersExportLayout? stored, Guid tenantId) =>
        new(
            tenantId,
            OrdersExportLayout.Effective(stored).Select(ToDto).ToArray(),
            stored?.Version ?? OrdersExportLayout.DefaultVersion);

    private static OrdersExportColumnDto ToDto(OrdersExportColumnSetting column)
    {
        if (column.Kind == OrdersExportColumnKind.Fixed)
        {
            return new OrdersExportColumnDto(
                nameof(OrdersExportColumnKind.Fixed), null, null, null, column.Header, column.Value, column.Visible);
        }

        // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
        var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
        return new OrdersExportColumnDto(
            nameof(OrdersExportColumnKind.Catalog),
            column.Key,
            OrdersExportColumnCatalog.Columns[index].DefaultHeader,
            index + 1,
            column.Header,
            null,
            column.Visible);
    }
}
