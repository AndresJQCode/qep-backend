using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una columna del layout efectivo (spec 2026-09-24, "Application y API"). <c>DefaultHeader</c>,
/// <c>DefaultPosition</c> y <c>DefaultVisible</c> viajan por columna (regla BFF del repo): la
/// pantalla los necesita como placeholder del input, para "restaurar" una sola y para "restaurar
/// todo" sin conocer el catálogo. <c>DefaultPosition</c> es 1-based (1..46 desde el ajuste
/// 2026-10-03), como la columna # de la tabla del spec: es la posición que la pantalla muestra.
/// <c>DefaultVisible</c> (ajuste 2026-10-02) existe porque ya no toda columna nace visible
/// —"coordinadora_city" y las "payment_method_N" nacen ocultas—, y
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

/// <summary>
/// El layout efectivo (D8), entero y en orden (D7). <c>Version</c> es la de la fila, o 1 si no hay
/// fila (D9).
///
/// <c>SheetName</c> (spec 2026-10-05) es el nombre efectivo de la hoja: el guardado, o
/// <see cref="OrdersExportLayout.DefaultSheetName"/> sin fila. <c>DefaultSheetName</c> viaja al
/// lado por la misma razón que <c>DefaultHeader</c> por columna (regla BFF): "Restaurar todo" lo
/// necesita y la pantalla no tiene por qué saberlo de memoria — si el default cambia acá, la
/// pantalla no se entera de otra forma. En el PUT, un <c>sheetName</c> nulo o ausente conserva el
/// actual (D6), para que un frontend anterior a este campo no le borre el nombre al tenant al
/// guardar columnas.
/// </summary>
public sealed record OrdersExportLayoutDto(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnDto> Columns,
    long Version,
    string SheetName,
    string DefaultSheetName);

public static class OrdersExportLayoutMappings
{
    public static OrdersExportLayoutDto ToDto(OrdersExportLayout? stored, Guid tenantId) =>
        new(
            tenantId,
            OrdersExportLayout.Effective(stored).Select(ToDto).ToArray(),
            stored?.Version ?? OrdersExportLayout.DefaultVersion,
            OrdersExportLayout.EffectiveSheetName(stored),
            OrdersExportLayout.DefaultSheetName);

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
