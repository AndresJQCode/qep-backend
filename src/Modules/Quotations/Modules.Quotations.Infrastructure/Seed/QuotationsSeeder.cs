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
    /// para el tenant (tipo de documento, bodega, transportadora, su NIT...) va como fija, vacía
    /// cuando la hoja exige la columna pero no tiene qué ponerle.
    ///
    /// La fecha del pedido se repite bajo FECHA, Bloq/act y Vencimiento, y el primer comprobante
    /// bajo dos encabezados: el ERP lee el mismo dato con varios nombres. Al final, ocultas, las
    /// llaves del catálogo que la hoja no usa — sin ellas <see cref="OrdersExportLayout.Effective(OrdersExportLayout?)"/>
    /// las completaría visibles y la hoja tendría columnas que el ERP no espera.
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
        OrdersExportColumnSetting.Catalog("bank", "Forma de pago 1", visible: true),
        OrdersExportColumnSetting.Catalog("proof_amount_1", "V. Consignacion 1", visible: true),
        OrdersExportColumnSetting.Fixed("Forma de pago 2", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("proof_amount_2", "V. Consignacion 2", visible: true),
        OrdersExportColumnSetting.Fixed("Verificado", "-1", visible: true),
        OrdersExportColumnSetting.Fixed("Anulado", "0", visible: true),
        OrdersExportColumnSetting.Catalog("product_code", "Cod. Producto", visible: true),
        OrdersExportColumnSetting.Fixed("Bodega", "Principal", visible: true),
        OrdersExportColumnSetting.Fixed("U.Medida", "Und.", visible: true),
        OrdersExportColumnSetting.Catalog("quantity", "Cantidad", visible: true),
        OrdersExportColumnSetting.Catalog("unit_price_without_tax", "Valor Unit", visible: true),
        OrdersExportColumnSetting.Fixed("IVA", "0.19", visible: true),
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
        OrdersExportColumnSetting.Catalog("city", "Ciudad (P4)", visible: true),
        OrdersExportColumnSetting.Fixed("Tipo Envio", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("document", "Documento (P5)", visible: true),
        OrdersExportColumnSetting.Catalog("order_number", "Pedido (P6)", visible: true),
        OrdersExportColumnSetting.Catalog("proof_amount_1", "V. Consignacion (P7)", visible: true),
        OrdersExportColumnSetting.Catalog("address", "Direccion (P8)", visible: true),
        OrdersExportColumnSetting.Catalog("notes", "Observaciones", visible: true),
        OrdersExportColumnSetting.Fixed("Guia P9", string.Empty, visible: true),
        OrdersExportColumnSetting.Catalog("phone", "Telefono (P10)", visible: true),
        OrdersExportColumnSetting.Fixed("valor flete", string.Empty, visible: true),
        OrdersExportColumnSetting.Fixed("# Rotulos", "1", visible: true),
        OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
        OrdersExportColumnSetting.Fixed("GeneraGuia", "X", visible: true),
        OrdersExportColumnSetting.Fixed("GeneraFactura", string.Empty, visible: true),
        OrdersExportColumnSetting.Fixed("Nit", "901851609", visible: true),
        .. HiddenCatalogKeys().Select(key => OrdersExportColumnSetting.Catalog(
            key,
            OrdersExportColumnCatalog.Columns[OrdersExportColumnCatalog.IndexOf(key)].DefaultHeader,
            visible: false)),
    ];

    // Las del catálogo que la hoja no usa, con su nombre por defecto. "Valor Unit" e "IVA" chocan
    // con dos visibles de la hoja, pero entre ocultas y visibles el encabezado puede repetirse.
    private static IEnumerable<string> HiddenCatalogKeys() =>
    [
        "unit_price",
        "tax",
        .. Enumerable.Range(2, OrdersExportColumnCatalog.PaymentDateColumns - 1).Select(number => $"payment_date_{number}"),
        "account",
        .. Enumerable.Range(1, OrdersExportColumnCatalog.PaymentDateColumns).Select(number => $"proof_url_{number}"),
        .. Enumerable.Range(3, OrdersExportColumnCatalog.PaymentDateColumns - 2).Select(number => $"proof_amount_{number}"),
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
        layout.Replace(OrdersExportColumns, now);
        dbContext.OrdersExportLayouts.Add(layout);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
