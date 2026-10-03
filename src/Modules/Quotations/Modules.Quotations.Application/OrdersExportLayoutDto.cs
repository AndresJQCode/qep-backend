using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una columna del layout efectivo (spec 2026-09-24, "Application y API"). <c>DefaultHeader</c>,
/// <c>DefaultPosition</c> y <c>DefaultVisible</c> viajan por columna (regla BFF del repo): la
/// pantalla los necesita como placeholder del input, para "restaurar" una sola y para "restaurar
/// todo" sin conocer el catálogo. <c>DefaultPosition</c> es 1-based (1..41), como la columna # de
/// la tabla del spec: es la posición que la pantalla muestra. <c>DefaultVisible</c> (ajuste
/// 2026-10-02) existe porque ya no toda columna nace visible —"coordinadora_city" nace oculta—, y
/// sin él "restaurar" la prendería: la pantalla tendría que saber de memoria cuáles son. Una llave
/// repetida (ajuste 2026-09-25) trae los mismos tres defectos en cada entrada. Nulos en una fija,
/// que no tiene defecto; <c>Value</c> nulo en una del catálogo.
/// </summary>
public sealed record OrdersExportColumnDto(
    string Kind,
    string? Key,
    string? DefaultHeader,
    int? DefaultPosition,
    bool? DefaultVisible,
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
                nameof(OrdersExportColumnKind.Fixed), null, null, null, null, column.Header, column.Value, column.Visible);
        }

        // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
        var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
        var catalogColumn = OrdersExportColumnCatalog.Columns[index];
        return new OrdersExportColumnDto(
            nameof(OrdersExportColumnKind.Catalog),
            column.Key,
            catalogColumn.DefaultHeader,
            index + 1,
            catalogColumn.DefaultVisible,
            column.Header,
            null,
            column.Visible);
    }
}
