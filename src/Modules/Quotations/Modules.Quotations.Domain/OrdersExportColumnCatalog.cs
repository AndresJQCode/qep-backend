namespace Modules.Quotations.Domain;

/// <summary>Una columna del catálogo del Excel de pedidos: la llave estable con la que el tenant la
/// homologa, el encabezado por defecto —el de hoy, sin cambios— y el ancho de la hoja.</summary>
public sealed record OrdersExportCatalogColumn(string Key, string DefaultHeader, double Width);

/// <summary>
/// Las columnas que el Excel de pedidos sabe producir, en su orden (spec 2026-09-24, homologación
/// de columnas). Vive en Domain porque <see cref="OrdersExportLayout.Effective(OrdersExportLayout?)"/>
/// lo necesita y Domain no referencia Application. La posición en <see cref="Columns"/> es el orden
/// del catálogo: el mismo que tenía <c>OrdersExportProcessor.Columns</c> antes de la homologación,
/// así que sin layout guardado el archivo es idéntico al de siempre.
///
/// Sólo el backend agrega o quita llaves, al sumar una columna. Una llave es un identificador y
/// no un texto: se compara ordinal y nunca se traduce ni se recorta. En el catálogo cada llave
/// está una vez; en el layout de un tenant puede repetirse bajo otros encabezados (ajuste
/// 2026-09-25).
/// </summary>
public static class OrdersExportColumnCatalog
{
    /// <summary>Cuántas fechas de pago tienen columna propia (ajuste 2026-09-20), y con ellas
    /// cuántos pares «V. Comprobante N» / «URL Comprobante N».</summary>
    public const int PaymentDateColumns = 5;

    public static readonly IReadOnlyList<OrdersExportCatalogColumn> Columns =
    [
        new("company", "EMPRESA", 30),
        new("product_code", "Cod. Producto", 18),
        new("quantity", "Cantidad", 12),
        new("unit_price", "Valor Unit", 16),
        new("tax", "IVA", 14),
        new("discount", "Descuento", 14),
        new("line_note", "Nota Detalle", 30),
        .. Enumerable.Range(1, PaymentDateColumns)
            .Select(number => new OrdersExportCatalogColumn($"payment_date_{number}", $"Fecha Pago {number}", 18)),
        new("city", "Ciudad", 20),
        new("document", "Documento", 16),
        new("order_number", "Pedido", 18),
        new("address", "Direccion", 40),
        new("notes", "Observaciones", 40),
        new("phone", "Telefono", 16),
        new("email", "Email", 30),
        new("advisor_code", "Cod. Asesor", 14),
        new("bank", "Banco", 24),
        new("account", "Cuenta", 20),
        .. Enumerable.Range(1, PaymentDateColumns).SelectMany(number => new OrdersExportCatalogColumn[]
        {
            new($"proof_amount_{number}", $"V. Comprobante {number}", 18),
            new($"proof_url_{number}", $"URL Comprobante {number}", 60),
        }),
        new("unit_price_without_tax", "Valor Unit sin IVA", 18),
        // Ajuste 2026-09-25, las dos que pide la hoja de importación del ERP (MIGRACION 1): el día
        // en que nació el pedido y a nombre de quién sale la factura. Al final, como toda columna
        // nueva, para no mover lo que el ERP ya importa sin layout guardado.
        new("order_date", "Fecha Pedido", 14),
        new("customer_name", "Cliente", 30),
    ];

    // Declarado después de Columns a propósito: los campos estáticos se inicializan en orden textual.
    private static readonly Dictionary<string, int> PositionByKey = Columns
        .Select((column, index) => (column.Key, Index: index))
        .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal);

    /// <summary>El índice de la llave en <see cref="Columns"/>, o −1 si no es del catálogo. Ordinal:
    /// <c>Company</c> no es <c>company</c> (Review Focus 5).</summary>
    public static int IndexOf(string key) =>
        PositionByKey.TryGetValue(key, out var index) ? index : -1;

    public static bool Contains(string key) => PositionByKey.ContainsKey(key);
}
