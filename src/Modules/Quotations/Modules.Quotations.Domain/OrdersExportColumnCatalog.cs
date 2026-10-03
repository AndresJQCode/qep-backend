namespace Modules.Quotations.Domain;

/// <summary>Una columna del catálogo del Excel de pedidos: la llave estable con la que el tenant la
/// homologa, el encabezado por defecto —el de hoy, sin cambios—, el ancho de la hoja y si sale
/// visible cuando el tenant no dijo nada de ella. <c>DefaultVisible</c> es <c>true</c> salvo en
/// una columna que no debe aparecer sola en los Excel que ya existen (ajuste 2026-10-02).</summary>
public sealed record OrdersExportCatalogColumn(string Key, string DefaultHeader, double Width, bool DefaultVisible = true);

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
    /// cuántos pares «V. Comprobante N» / «URL Comprobante N» y cuántas «Forma de pago N» (ajuste
    /// 2026-10-03).</summary>
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
        // Ajuste 2026-09-26: el número de documento de identidad de quien nombra "Cliente", que es
        // lo que el ERP del tenant importa en "Documento (P5)". No reemplaza a "document", que
        // sigue siendo el CUC: otro tenant puede estar leyéndolo de ahí.
        new("customer_identification", "Documento de identidad", 18),
        // Ajuste 2026-09-26: el banco y el número de cuenta en una sola celda, que es lo que el ERP
        // del tenant importa como forma de pago, y la suma de todos los comprobantes del pedido, que
        // importa como valor consignado. No reemplazan a "bank", "account" ni "proof_amount_N":
        // otros ERP pueden estar leyéndolos por separado.
        new("bank_account", "Banco y cuenta", 36),
        new("proof_amount_total", "Total consignado", 18),
        // Ajuste 2026-09-26: la tasa de IVA de la línea como fracción (19 % → 0,19), que es lo que
        // el ERP del tenant importa en "IVA". Por línea y no fija: hay productos con otra tarifa. No
        // reemplaza a "tax", que es el monto y otro tenant puede estar leyéndolo.
        new("tax_rate", "Tasa IVA", 12),
        // Ajuste 2026-09-26: el NIT de la empresa por la que se factura —la misma que nombra
        // "company"—, que el ERP del tenant importa en "Nit". Antes era una fija con el NIT escrito a
        // mano, que dejaba de ser cierto en cuanto la cotización se facturaba por otra empresa.
        new("company_tax_id", "NIT Empresa", 18),
        // Ajuste 2026-10-02: la ciudad como la escribe Coordinadora ("ABEJORRAL (ANT)"), que el
        // tenant importa en su ERP para generar la guía de envío. No reemplaza a "city", que es el
        // nombre del DANE y otro tenant puede estar leyéndolo. Oculta por defecto porque el owner
        // pidió que no apareciera en los Excel que ya existen hasta que un tenant la prenda.
        new("coordinadora_city", "Ciudad Coordinadora", 30, DefaultVisible: false),
        // Ajuste 2026-10-03: el banco con la cuenta, una por comprobante. "bank_account" es del
        // pedido, así que repetida bajo "Forma de pago 1" y "Forma de pago 2" llenaba las dos aunque
        // el pedido tuviera un solo comprobante; "payment_method_N" sólo se llena si existe el
        // comprobante N. No reemplaza a "bank_account": otros ERP pueden estar leyéndolo. Ocultas
        // por defecto para que ningún Excel que ya existe cambie, y al final por lo mismo.
        .. Enumerable.Range(1, PaymentDateColumns)
            .Select(number => new OrdersExportCatalogColumn(
                $"payment_method_{number}", $"Forma de pago {number}", 36, DefaultVisible: false)),
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
