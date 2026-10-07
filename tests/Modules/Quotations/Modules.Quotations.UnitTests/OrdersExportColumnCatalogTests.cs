using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El catálogo del Excel de pedidos (spec 2026-09-24): las 49 columnas de hoy, con la llave estable
/// con la que el tenant las homologa. Su orden es el orden del archivo sin layout guardado, así que
/// se fija contra la lista del processor y no al revés.
/// </summary>
public sealed class OrdersExportColumnCatalogTests
{
    private static readonly string[] Keys =
    [
        "company", "product_code", "quantity", "unit_price", "tax", "discount", "line_note",
        "payment_date_1", "payment_date_2", "payment_date_3", "payment_date_4", "payment_date_5",
        "city", "document", "order_number", "address", "notes", "phone", "email",
        "advisor_code", "bank", "account",
        "proof_amount_1", "proof_url_1", "proof_amount_2", "proof_url_2", "proof_amount_3", "proof_url_3",
        "proof_amount_4", "proof_url_4", "proof_amount_5", "proof_url_5",
        "unit_price_without_tax", "order_date", "customer_name", "customer_identification",
        "bank_account", "proof_amount_total", "tax_rate", "company_tax_id", "coordinadora_city",
        "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5",
        "carrier", "order_total", "retention_amount",
    ];

    private static readonly string[] Headers =
    [
        "EMPRESA", "Cod. Producto",
        "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
        "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
        "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
        "Cod. Asesor", "Banco", "Cuenta",
        "V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2",
        "V. Comprobante 3", "URL Comprobante 3", "V. Comprobante 4", "URL Comprobante 4",
        "V. Comprobante 5", "URL Comprobante 5",
        "Valor Unit sin IVA", "Fecha Pedido", "Cliente", "Documento de identidad",
        "Banco y cuenta", "Total consignado", "Tasa IVA", "NIT Empresa", "Ciudad Coordinadora",
        "Forma de pago 1", "Forma de pago 2", "Forma de pago 3", "Forma de pago 4", "Forma de pago 5",
        "Transportadora", "Total facturado", "Retencion",
    ];

    [Fact]
    public void HasTheFortyNineColumnsOfTheSpecInItsOrder()
    {
        Assert.Equal(49, OrdersExportColumnCatalog.Columns.Count);
        Assert.Equal(Keys, OrdersExportColumnCatalog.Columns.Select(column => column.Key));
        Assert.Equal(Headers, OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader));
    }

    // Las dos que pidió la hoja de importación del ERP (MIGRACION 1) van al final, como toda columna
    // nueva: no mueven lo que el ERP ya importa sin layout guardado.
    [Fact]
    public void OrderDateAndCustomerNameAreAppendedAtTheEnd()
    {
        Assert.Equal(new OrdersExportCatalogColumn("order_date", "Fecha Pedido", 14), OrdersExportColumnCatalog.Columns[33]);
        Assert.Equal(new OrdersExportCatalogColumn("customer_name", "Cliente", 30), OrdersExportColumnCatalog.Columns[34]);
    }

    // Ajuste 2026-09-26: el número de documento de identidad de quien nombra "Cliente". Va detrás de
    // todas, y "document" sigue siendo el CUC: otro ERP puede estar leyéndolo de ahí.
    [Fact]
    public void CustomerIdentificationIsAppendedAfterCustomerName()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("customer_identification", "Documento de identidad", 18),
            OrdersExportColumnCatalog.Columns[35]);
        Assert.Equal(35, OrdersExportColumnCatalog.IndexOf("customer_identification"));
        Assert.Equal(13, OrdersExportColumnCatalog.IndexOf("document"));
    }

    // Ajuste 2026-09-26: banco y cuenta en una sola celda, y el total de todos los comprobantes del
    // pedido. Detrás de todas, y sin tocar "bank", "account" ni "proof_amount_N": otros ERP los leen.
    [Fact]
    public void BankAccountAndProofAmountTotalAreAppendedAtTheEnd()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("bank_account", "Banco y cuenta", 36),
            OrdersExportColumnCatalog.Columns[36]);
        Assert.Equal(
            new OrdersExportCatalogColumn("proof_amount_total", "Total consignado", 18),
            OrdersExportColumnCatalog.Columns[37]);
        Assert.Equal(20, OrdersExportColumnCatalog.IndexOf("bank"));
        Assert.Equal(22, OrdersExportColumnCatalog.IndexOf("proof_amount_1"));
    }

    // Ajuste 2026-09-26: la tasa de IVA de cada línea como fracción, que es lo que el ERP importa en
    // "IVA". Detrás de todas, y "tax" sigue siendo el monto: otros ERP lo leen ahí.
    [Fact]
    public void TaxRateIsAppendedAtTheEnd()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("tax_rate", "Tasa IVA", 12),
            OrdersExportColumnCatalog.Columns[38]);
        Assert.Equal(38, OrdersExportColumnCatalog.IndexOf("tax_rate"));
        Assert.Equal(4, OrdersExportColumnCatalog.IndexOf("tax"));
    }

    // Ajuste 2026-09-26: el NIT de la empresa por la que se factura —la misma de "EMPRESA"—, que el
    // ERP del tenant importa en "Nit". Detrás de todas, como toda columna nueva; desde el ajuste
    // 2026-10-02 la sigue "coordinadora_city".
    [Fact]
    public void CompanyTaxIdIsAppendedAtTheEnd()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("company_tax_id", "NIT Empresa", 18),
            OrdersExportColumnCatalog.Columns[39]);
        Assert.Equal(39, OrdersExportColumnCatalog.IndexOf("company_tax_id"));
    }

    // Ajuste 2026-10-02: la ciudad como la escribe Coordinadora, para la guía. Detrás de todas, y
    // oculta por defecto: el owner pidió que no apareciera en los Excel que ya existen hasta que un
    // tenant la prenda. "city" sigue siendo el nombre del DANE. Desde el ajuste 2026-10-03 la siguen
    // las "Forma de pago N".
    [Fact]
    public void CoordinadoraCityIsAppendedAfterCompanyTaxIdHiddenByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("coordinadora_city", "Ciudad Coordinadora", 30, DefaultVisible: false),
            OrdersExportColumnCatalog.Columns[40]);
        Assert.Equal(40, OrdersExportColumnCatalog.IndexOf("coordinadora_city"));
        Assert.Equal(12, OrdersExportColumnCatalog.IndexOf("city"));
    }

    // Ajuste 2026-10-03: el banco con la cuenta, una por comprobante. "bank_account" es del pedido,
    // así que repetida bajo "Forma de pago 1" y "Forma de pago 2" llenaba las dos aunque hubiera un
    // solo comprobante; ésta sólo se llena si existe el comprobante N. Ocultas, para que ningún Excel
    // que ya existe cambie; "bank_account" sigue igual, otros ERP lo leen. Desde el ajuste 2026-10-05
    // las sigue "carrier".
    [Fact]
    public void PaymentMethodsAreAppendedAfterCoordinadoraCityHiddenByDefault()
    {
        Assert.Equal(
            Enumerable.Range(1, OrdersExportColumnCatalog.PaymentDateColumns)
                .Select(number => new OrdersExportCatalogColumn(
                    $"payment_method_{number}", $"Forma de pago {number}", 36, DefaultVisible: false)),
            OrdersExportColumnCatalog.Columns.Skip(41).Take(OrdersExportColumnCatalog.PaymentDateColumns));
        Assert.Equal(45, OrdersExportColumnCatalog.IndexOf("payment_method_5"));
        Assert.Equal(36, OrdersExportColumnCatalog.IndexOf("bank_account"));
        Assert.Equal(
            ["coordinadora_city", "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5", "carrier"],
            OrdersExportColumnCatalog.Columns.Where(column => !column.DefaultVisible).Select(column => column.Key));
    }

    // Spec 2026-10-05 (recoger en tienda): la transportadora del pedido —"Recoger en tienda" si el
    // cliente pasa a recogerlo, "Coordinadora" si no—. Detrás de las "Forma de pago N" y oculta,
    // mismo criterio que "coordinadora_city": un layout ya guardado la recibe sin columna sorpresa.
    // Desde el ajuste 2026-10-05 la sigue "order_total".
    [Fact]
    public void CarrierIsAppendedAfterThePaymentMethodsHiddenByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("carrier", "Transportadora", 20, DefaultVisible: false),
            OrdersExportColumnCatalog.Columns[46]);
        Assert.Equal(46, OrdersExportColumnCatalog.IndexOf("carrier"));
    }

    // Ajuste 2026-10-05: lo facturado del pedido (Quotation.Total), repetido en cada línea. Al final,
    // como toda columna nueva, para no mover lo que ya sale; visible por decisión del owner, así que
    // también la ganan los layouts guardados. Desde el ajuste 2026-10-06 la sigue "retention_amount".
    [Fact]
    public void OrderTotalIsAppendedAfterTheCarrierVisibleByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("order_total", "Total facturado", 18),
            OrdersExportColumnCatalog.Columns[47]);
        Assert.Equal(47, OrdersExportColumnCatalog.IndexOf("order_total"));
        Assert.Equal(37, OrdersExportColumnCatalog.IndexOf("proof_amount_total"));
    }

    // Ajuste 2026-10-06: la retención del pedido (Quotation.RetentionAmount), repetida en cada línea,
    // justo detrás de "Total facturado": con las dos el ERP cuadra lo facturado contra lo que de
    // verdad se cobra. Visible por defecto, como "order_total", así que también la ganan los layouts
    // guardados.
    [Fact]
    public void RetentionAmountIsAppendedAtTheEndVisibleByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("retention_amount", "Retencion", 16),
            OrdersExportColumnCatalog.Columns[^1]);
        Assert.Equal(48, OrdersExportColumnCatalog.IndexOf("retention_amount"));
        Assert.Equal(OrdersExportColumnCatalog.IndexOf("order_total") + 1, OrdersExportColumnCatalog.IndexOf("retention_amount"));
    }

    // Las llaves son identificadores: únicas, y el tenant no puede inventar ni repetir una.
    [Fact]
    public void KeysAreUnique()
    {
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Count,
            OrdersExportColumnCatalog.Columns.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count());
    }

    // Encabezado y ancho son los del processor de hoy, en el mismo orden: sin layout guardado el
    // archivo no cambia. Cuando Task 8 derive Columns del catálogo, esta prueba sigue siendo la red.
    // Sólo las visibles por defecto (ajuste 2026-10-02): la oculta no está en el archivo de siempre.
    [Fact]
    public void MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth()
    {
        Assert.Equal(
            OrdersExportColumnCatalog.Columns
                .Where(column => column.DefaultVisible)
                .Select(column => new ExportColumn(column.DefaultHeader, column.Width)),
            OrdersExportProcessor.Columns);
        Assert.Equal(42, OrdersExportProcessor.Columns.Count);
    }

    // Review Focus 5: la llave se compara ordinal. "Company" no existe.
    [Fact]
    public void IndexOfIsOrdinal()
    {
        Assert.Equal(0, OrdersExportColumnCatalog.IndexOf("company"));
        Assert.Equal(32, OrdersExportColumnCatalog.IndexOf("unit_price_without_tax"));
        Assert.Equal(34, OrdersExportColumnCatalog.IndexOf("customer_name"));
        Assert.Equal(-1, OrdersExportColumnCatalog.IndexOf("Company"));
        Assert.Equal(-1, OrdersExportColumnCatalog.IndexOf(" company"));
        Assert.True(OrdersExportColumnCatalog.Contains("email"));
        Assert.False(OrdersExportColumnCatalog.Contains("EMAIL"));
    }

    [Fact]
    public void PaymentColumnsComeInFives()
    {
        Assert.Equal(5, OrdersExportColumnCatalog.PaymentDateColumns);
        Assert.Equal(5, OrdersExportColumnCatalog.Columns.Count(column => column.Key.StartsWith("payment_date_", StringComparison.Ordinal)));
        Assert.Equal(5, OrdersExportColumnCatalog.Columns.Count(column => column.Key.StartsWith("proof_url_", StringComparison.Ordinal)));
    }
}
