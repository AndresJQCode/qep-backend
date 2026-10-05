using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure.Seed;

/// <summary>
/// La mitad de Quotations de la semilla de arranque: el formato del numero de pedido del tenant
/// sembrado y, desde el 2026-09-25, el layout de su Excel de pedidos
/// (<see cref="SeedOrdersExportLayoutAsync"/>). El cliente trae su propia serie, asi que sus pedidos salen como <c>PW234235</c> y no
/// como <c>PED-2026-0001</c> — el caso canonico del README (§ Numeracion de documentos por tenant).
///
/// Solo el formato, no el consecutivo: <c>order_number_counters</c> no se toca porque no hay un
/// numero de arranque conocido, y la serie sigue desde donde este. Tampoco se configura
/// <c>quotation</c>: las cotizaciones siguen con el default.
///
/// Idempotente **por (tenant, tipo)**, que es la clave primaria de la tabla. Solo crea: si ya hay
/// fila de pedidos para el tenant, se deja tal cual, aunque no sea la de la semilla. La tabla se
/// configura a mano con el runbook del README, y un reinicio de pod no puede pisar lo que alguien
/// decidio con SQL.
/// </summary>
public static class QuotationsSeeder
{
    private const string OrderDocumentType = "order";

    /// <summary>
    /// Pasa por <c>DocumentNumberFormat.Create</c> aunque los valores sean constantes: si alguien
    /// los cambia por uno que el CHECK rechaza, el error sale con el codigo del dominio y en la
    /// primera linea del arranque, no como una <c>PostgresException</c> al guardar.
    /// </summary>
    private static readonly DocumentNumberFormat OrderFormat =
        DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: string.Empty, minDigits: 1);

    /// <summary>Crea el formato de pedidos del tenant si todavia no tiene uno.</summary>
    public static async Task SeedOrderNumberingAsync(
        this IServiceProvider services,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();

        var alreadyConfigured = await dbContext.DocumentNumberingFormats.AnyAsync(
            format => format.TenantId == tenantId && format.DocumentType == OrderDocumentType,
            cancellationToken);
        if (alreadyConfigured)
        {
            return;
        }

        dbContext.DocumentNumberingFormats.Add(new DocumentNumberingFormat
        {
            TenantId = tenantId,
            DocumentType = OrderDocumentType,
            Prefix = OrderFormat.Prefix,
            IncludeYear = OrderFormat.IncludeYear,
            YearSeparator = OrderFormat.YearSeparator,
            MinDigits = OrderFormat.MinDigits,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// El layout del Excel de pedidos del tenant sembrado (ajuste 2026-09-25): reproduce la hoja
    /// de importación de su ERP, «MIGRACION 1», columna por columna y en su orden. Lo que el ERP
    /// pide y el backend conoce sale del catálogo con el encabezado de la hoja; lo que es constante
    /// para el tenant (tipo de documento, bodega, transportadora...) va como fija, vacía
    /// cuando la hoja exige la columna pero no tiene qué ponerle.
    ///
    /// La fecha del pedido se repite bajo FECHA, Bloq/act y Vencimiento: el ERP lee el mismo dato
    /// con varios nombres. El banco con su cuenta va en las dos formas de pago, pero cada una con su
    /// comprobante (ajuste 2026-10-03): sin el comprobante N, la forma de pago N sale vacía. Al
    /// final, ocultas, las llaves del catálogo que la hoja no usa — sin ellas
    /// <see cref="OrdersExportLayout.Effective(OrdersExportLayout?)"/> las completaría visibles y la
    /// hoja tendría columnas que el ERP no espera.
    ///
    /// Pasa por <see cref="OrdersExportLayout.Replace"/> y no se escribe el JSON a mano: un
    /// encabezado duplicado o una fija de más revientan con el código del dominio en el arranque.
    /// </summary>
    private static readonly OrdersExportColumnSetting[] OrdersExportColumns =
    [
        OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
        OrdersExportColumnSetting.Fixed("DOC.", "FV", visible: true),
        OrdersExportColumnSetting.Fixed("PREFIJO", "PM", visible: true),
        OrdersExportColumnSetting.Fixed("No. Doc.", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("order_date", "FECHA", visible: true),
        OrdersExportColumnSetting.Fixed("Tercero Externo", "9999", visible: true),
        OrdersExportColumnSetting.Catalog("customer_name", "Nota Encab.", visible: true),
        OrdersExportColumnSetting.Catalog("advisor_code", "Tercero Interno", visible: true),
        OrdersExportColumnSetting.Fixed("Doc. Externo", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("order_date", "Bloq/act", visible: true),
        // Ajuste 2026-09-26: el ERP lee en las dos formas de pago el banco con el número de cuenta,
        // no sólo el banco. "bank" queda entre las ocultas. Ajuste 2026-10-03: cada una con su
        // comprobante —"payment_method_N" va vacía si el pedido no tiene el comprobante N—, no
        // "bank_account", que es del pedido y llenaba la 2 con una sola consignación. "bank_account"
        // queda entre las ocultas.
        OrdersExportColumnSetting.Catalog("payment_method_1", "Forma de pago 1", visible: true),
        OrdersExportColumnSetting.Catalog("proof_amount_1", "V. Consignacion 1", visible: true),
        OrdersExportColumnSetting.Catalog("payment_method_2", "Forma de pago 2", visible: true),
        OrdersExportColumnSetting.Catalog("proof_amount_2", "V. Consignacion 2", visible: true),
        OrdersExportColumnSetting.Fixed("Verificado", "-1", visible: true),
        OrdersExportColumnSetting.Fixed("Anulado", "0", visible: true),
        OrdersExportColumnSetting.Catalog("product_code", "Cod. Producto", visible: true),
        OrdersExportColumnSetting.Fixed("Bodega", "Principal", visible: true),
        OrdersExportColumnSetting.Fixed("U.Medida", "Und.", visible: true),
        OrdersExportColumnSetting.Catalog("quantity", "Cantidad", visible: true),
        // Ajuste 2026-10-03: el ERP lee acá el precio por unidad con el descuento ya aplicado y sin
        // IVA, que es "unit_price". "unit_price_without_tax" (sin IVA, pero antes del descuento)
        // queda entre las ocultas.
        OrdersExportColumnSetting.Catalog("unit_price", "Valor Unit", visible: true),
        // Ajuste 2026-09-26: la tasa de cada línea y no un 0.19 fijo, porque hay productos con otra
        // tarifa. "tax" (el monto) queda entre las ocultas.
        OrdersExportColumnSetting.Catalog("tax_rate", "IVA", visible: true),
        OrdersExportColumnSetting.Catalog("discount", "Descuento", visible: true),
        OrdersExportColumnSetting.Fixed("Lote", string.Empty, visible: true),
        OrdersExportColumnSetting.Fixed("Centro costos", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("line_note", "Nota Detalle", visible: true),
        OrdersExportColumnSetting.Catalog("order_date", "Vencimiento", visible: true),
        OrdersExportColumnSetting.Fixed("Factor Conversion Cantidad", "0", visible: true),
        OrdersExportColumnSetting.Fixed("Factor conversion", "0", visible: true),
        OrdersExportColumnSetting.Catalog("payment_date_1", "Fecha Pago (P1)", visible: true),
        OrdersExportColumnSetting.Fixed("Transportadora (P2)", "Coordinadora", visible: true),
        OrdersExportColumnSetting.Fixed("Flete (P3)", "CONTRAENTREGA", visible: true),
        // Ajuste 2026-10-02: la hoja ya fija "Transportadora (P2)" = Coordinadora, así que la ciudad
        // va como la escribe Coordinadora ("ABEJORRAL (ANT)"), no con el nombre del DANE. "city"
        // queda entre las ocultas.
        OrdersExportColumnSetting.Catalog("coordinadora_city", "Ciudad (P4)", visible: true),
        OrdersExportColumnSetting.Fixed("Tipo Envio", string.Empty, visible: true),
        // Ajuste 2026-09-26: el ERP lee acá el documento de identidad de quien se factura, no el
        // CUC. "document" (el CUC) queda entre las ocultas.
        OrdersExportColumnSetting.Catalog("customer_identification", "Documento (P5)", visible: true),
        OrdersExportColumnSetting.Catalog("order_number", "Pedido (P6)", visible: true),
        // Ajuste 2026-09-26: el valor consignado es el total de todos los comprobantes del pedido, no
        // el primero, que sigue en "V. Consignacion 1".
        OrdersExportColumnSetting.Catalog("proof_amount_total", "V. Consignacion (P7)", visible: true),
        OrdersExportColumnSetting.Catalog("address", "Direccion (P8)", visible: true),
        OrdersExportColumnSetting.Catalog("notes", "Observaciones", visible: true),
        OrdersExportColumnSetting.Fixed("Guia P9", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("phone", "Telefono (P10)", visible: true),
        OrdersExportColumnSetting.Fixed("valor flete", string.Empty, visible: true),
        OrdersExportColumnSetting.Fixed("# Rotulos", "1", visible: true),
        OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
        OrdersExportColumnSetting.Fixed("GeneraGuia", string.Empty, visible: true),
        OrdersExportColumnSetting.Fixed("GeneraFactura", string.Empty, visible: true),
        // Ajuste 2026-09-26: el NIT de la empresa por la que se factura —la de "EMPRESA"—, no uno
        // escrito a mano, que salía mal en cuanto la cotización se facturaba por otra empresa.
        OrdersExportColumnSetting.Catalog("company_tax_id", "Nit", visible: true),
        .. HiddenCatalogKeys().Select(key => OrdersExportColumnSetting.Catalog(
            key,
            OrdersExportColumnCatalog.Columns[OrdersExportColumnCatalog.IndexOf(key)].DefaultHeader,
            visible: false)),
    ];

    /// <summary>El nombre con el que el importador del ERP del tenant busca la hoja (spec
    /// 2026-10-05, D8). Sin tilde: así lo pide ese importador, aunque el dominio acepte tildes.</summary>
    private const string OrdersExportSheetName = "MIGRACION 1";

    // Las del catálogo que la hoja no usa, con su nombre por defecto. "IVA" choca con una visible
    // de la hoja, pero entre ocultas y visibles el encabezado puede repetirse.
    private static IEnumerable<string> HiddenCatalogKeys() =>
    [
        "unit_price_without_tax",
        "tax",
        "document",
        "city",
        .. Enumerable.Range(2, OrdersExportColumnCatalog.PaymentDateColumns - 1).Select(number => $"payment_date_{number}"),
        "bank",
        "account",
        .. Enumerable.Range(1, OrdersExportColumnCatalog.PaymentDateColumns).Select(number => $"proof_url_{number}"),
        .. Enumerable.Range(3, OrdersExportColumnCatalog.PaymentDateColumns - 2).Select(number => $"proof_amount_{number}"),
        "bank_account",
        // Nacen ocultas y Effective las completaría así, pero la lista las nombra igual: que la hoja
        // diga en un solo lugar todo lo que no usa.
        .. Enumerable.Range(3, OrdersExportColumnCatalog.PaymentDateColumns - 2).Select(number => $"payment_method_{number}"),
    ];

    /// <summary>
    /// Crea el layout del Excel de pedidos del tenant si todavía no tiene uno. Mismo criterio que
    /// <see cref="SeedOrderNumberingAsync"/>: sólo crea, porque el tenant pudo haber cambiado sus
    /// columnas desde la pantalla y un reinicio de pod no puede pisarlas.
    /// </summary>
    public static async Task SeedOrdersExportLayoutAsync(
        this IServiceProvider services,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();

        var alreadyConfigured = await dbContext.OrdersExportLayouts.AnyAsync(
            layout => layout.TenantId == tenantId, cancellationToken);
        if (alreadyConfigured)
        {
            return;
        }

        // Como el primer PUT: el por defecto en versión 1, y Replace lo deja en 2.
        var now = DateTimeOffset.UtcNow;
        var layout = OrdersExportLayout.CreateDefault(tenantId, now);
        layout.Replace(OrdersExportColumns, OrdersExportSheetName, now);
        dbContext.OrdersExportLayouts.Add(layout);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
