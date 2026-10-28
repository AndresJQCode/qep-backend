using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El catálogo del Excel de pedidos (spec 2026-09-24): las 81 columnas de hoy, con la llave estable
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
        "unit_price_without_tax", "order_date",
        "bank_account", "proof_amount_total", "tax_rate", "company_tax_id", "coordinadora_city",
        "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5",
        "carrier", "order_total", "retention_amount",
        "freight_amount", "freight_without_tax", "freight_tax", "freight_charge_mode", "cash_on_delivery",
        "variant",
        "customer_name", "customer_identification_type", "customer_identification", "customer_person_type",
        "customer_address", "customer_city", "customer_department", "customer_phone", "customer_email",
        "billing_name", "billing_identification_type", "billing_identification_number", "billing_person_type",
        "billing_address", "billing_city", "billing_department", "billing_phone", "billing_email",
        "shipping_name", "shipping_identification_type", "shipping_identification_number", "shipping_person_type",
        "shipping_address", "shipping_city", "shipping_city_coordinadora", "shipping_department", "shipping_phone", "shipping_email",
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
        "Valor Unit sin IVA", "Fecha Pedido",
        "Banco y cuenta", "Total consignado", "Tasa IVA", "NIT Empresa", "Ciudad Coordinadora",
        "Forma de pago 1", "Forma de pago 2", "Forma de pago 3", "Forma de pago 4", "Forma de pago 5",
        "Transportadora", "Total facturado", "Retencion",
        "Valor Flete", "Flete sin IVA", "IVA Flete", "Pago Flete", "Contraentrega",
        "Variante",
        "Cliente", "Tipo de identificación cliente", "Documento de identidad", "Tipo de persona cliente",
        "Dirección cliente", "Ciudad cliente", "Departamento cliente", "Teléfono cliente", "Email cliente",
        "Nombre facturación", "Tipo de identificación facturación", "Identificación facturación",
        "Tipo de persona facturación", "Dirección facturación", "Ciudad facturación", "Departamento facturación",
        "Teléfono facturación", "Email facturación",
        "Nombre envío", "Tipo de identificación envío", "Identificación envío", "Tipo de persona envío",
        "Dirección envío", "Ciudad envío", "Ciudad Coordinadora envío", "Departamento envío", "Teléfono envío", "Email envío",
    ];

    [Fact]
    public void HasTheEightyOneColumnsOfTheSpecInItsOrder()
    {
        Assert.Equal(81, OrdersExportColumnCatalog.Columns.Count);
        Assert.Equal(Keys, OrdersExportColumnCatalog.Columns.Select(column => column.Key));
        Assert.Equal(Headers, OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader));
    }

    // Las dos que pidió la hoja de importación del ERP (MIGRACION 1) van al final, como toda columna
    // nueva: no mueven lo que el ERP ya importa sin layout guardado.
    // Spec 2026-10-17 flete §7: el flete va al final; sólo "Valor Flete" es visible (D-F10).
    [Fact]
    public void FreightColumnsAreAppendedAtTheEnd()
    {
        Assert.Equal(new OrdersExportCatalogColumn("freight_amount", "Valor Flete", 16), OrdersExportColumnCatalog.Columns[47]);
        Assert.Equal(new OrdersExportCatalogColumn("freight_without_tax", "Flete sin IVA", 16, DefaultVisible: false), OrdersExportColumnCatalog.Columns[48]);
        Assert.Equal(new OrdersExportCatalogColumn("freight_tax", "IVA Flete", 16, DefaultVisible: false), OrdersExportColumnCatalog.Columns[49]);
        Assert.Equal(new OrdersExportCatalogColumn("freight_charge_mode", "Pago Flete", 16, DefaultVisible: false), OrdersExportColumnCatalog.Columns[50]);
        Assert.Equal(new OrdersExportCatalogColumn("cash_on_delivery", "Contraentrega", 16, DefaultVisible: false), OrdersExportColumnCatalog.Columns[51]);
    }

    // Spec 2026-10-20 variantes §7: el nombre visible de la variante, al final como toda columna nueva y
    // oculta por defecto, para que ningún Excel que ya existe —ni la hoja del ERP— gane una columna.
    // "product_code" no cambia de llave ni de encabezado: lo que cambia es que lleva el `Code` de la variante.
    [Fact]
    public void VariantIsAppendedAfterTheFreightColumnsHiddenByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("variant", "Variante", 36, DefaultVisible: false),
            OrdersExportColumnCatalog.Columns[52]);
        Assert.Equal(52, OrdersExportColumnCatalog.IndexOf("variant"));
        Assert.Equal(new OrdersExportCatalogColumn("product_code", "Cod. Producto", 18), OrdersExportColumnCatalog.Columns[1]);
    }

    // Ajuste 2026-10-28: dos grupos al final de todo. «customer_*» es la ficha del cliente («Cliente» y
    // «Documento de identidad» siguen visibles, el resto oculto); «billing_*» son los datos para la factura,
    // todos ocultos. Reemplazan a «customer_own_*» y a las «billing_*» sueltas.
    [Fact]
    public void CustomerGroupIsAppendedAfterVariant()
    {
        Assert.Equal(
            [
                new OrdersExportCatalogColumn("customer_name", "Cliente", 30),
                new OrdersExportCatalogColumn("customer_identification_type", "Tipo de identificación cliente", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_identification", "Documento de identidad", 18),
                new OrdersExportCatalogColumn("customer_person_type", "Tipo de persona cliente", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_address", "Dirección cliente", 40, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_city", "Ciudad cliente", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_department", "Departamento cliente", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_phone", "Teléfono cliente", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("customer_email", "Email cliente", 30, DefaultVisible: false),
            ],
            OrdersExportColumnCatalog.Columns.Skip(53).Take(9));
        Assert.Equal(53, OrdersExportColumnCatalog.IndexOf("customer_name"));
        Assert.Equal(53, OrdersExportColumnCatalog.IndexOf("variant") + 1);
    }

    [Fact]
    public void BillingGroupIsAppendedAfterCustomerHiddenByDefault()
    {
        Assert.Equal(
            [
                new OrdersExportCatalogColumn("billing_name", "Nombre facturación", 30, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_identification_type", "Tipo de identificación facturación", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_identification_number", "Identificación facturación", 18, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_person_type", "Tipo de persona facturación", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_address", "Dirección facturación", 40, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_city", "Ciudad facturación", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_department", "Departamento facturación", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_phone", "Teléfono facturación", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("billing_email", "Email facturación", 30, DefaultVisible: false),
            ],
            OrdersExportColumnCatalog.Columns.Skip(62).Take(9));
        Assert.Equal(62, OrdersExportColumnCatalog.IndexOf("billing_name"));
        Assert.Equal(70, OrdersExportColumnCatalog.IndexOf("billing_email"));
    }

    // Ajuste 2026-10-28: el grupo de envío cierra el catálogo, todo oculto.
    [Fact]
    public void ShippingGroupIsAppendedLastHiddenByDefault()
    {
        Assert.Equal(
            [
                new OrdersExportCatalogColumn("shipping_name", "Nombre envío", 30, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_identification_type", "Tipo de identificación envío", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_identification_number", "Identificación envío", 18, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_person_type", "Tipo de persona envío", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_address", "Dirección envío", 40, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_city", "Ciudad envío", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_city_coordinadora", "Ciudad Coordinadora envío", 30, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_department", "Departamento envío", 20, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_phone", "Teléfono envío", 16, DefaultVisible: false),
                new OrdersExportCatalogColumn("shipping_email", "Email envío", 30, DefaultVisible: false),
            ],
            OrdersExportColumnCatalog.Columns.Skip(71));
        Assert.Equal(71, OrdersExportColumnCatalog.IndexOf("shipping_name"));
        Assert.Equal(OrdersExportColumnCatalog.IndexOf("shipping_city") + 1, OrdersExportColumnCatalog.IndexOf("shipping_city_coordinadora"));
        Assert.Equal(OrdersExportColumnCatalog.Columns.Count - 1, OrdersExportColumnCatalog.IndexOf("shipping_email"));
    }

    // Se retiraron las llaves «customer_own_*»: un layout guardado que las nombre las pierde (Effective descarta
    // las desconocidas).
    [Fact]
    public void TheRetiredCustomerOwnKeysAreGone()
    {
        Assert.False(OrdersExportColumnCatalog.Contains("customer_own_name"));
        Assert.False(OrdersExportColumnCatalog.Contains("customer_own_identification"));
    }

    [Fact]
    public void OrderDateStaysAndCustomerNameMovedToTheEnd()
    {
        Assert.Equal(new OrdersExportCatalogColumn("order_date", "Fecha Pedido", 14), OrdersExportColumnCatalog.Columns[33]);
        Assert.Equal(new OrdersExportCatalogColumn("customer_name", "Cliente", 30), OrdersExportColumnCatalog.Columns[53]);
    }

    // Ajuste 2026-09-26: el número de documento de identidad de quien nombra "Cliente". Va detrás de
    // todas, y "document" sigue siendo el CUC: otro ERP puede estar leyéndolo de ahí.
    [Fact]
    public void CustomerIdentificationIsTheFichasNumberInTheCustomerGroup()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("customer_identification", "Documento de identidad", 18),
            OrdersExportColumnCatalog.Columns[55]);
        Assert.Equal(55, OrdersExportColumnCatalog.IndexOf("customer_identification"));
        Assert.Equal(13, OrdersExportColumnCatalog.IndexOf("document"));
    }

    // Ajuste 2026-09-26: banco y cuenta en una sola celda, y el total de todos los comprobantes del
    // pedido. Detrás de todas, y sin tocar "bank", "account" ni "proof_amount_N": otros ERP los leen.
    [Fact]
    public void BankAccountAndProofAmountTotalAreAppendedAtTheEnd()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("bank_account", "Banco y cuenta", 36),
            OrdersExportColumnCatalog.Columns[34]);
        Assert.Equal(
            new OrdersExportCatalogColumn("proof_amount_total", "Total consignado", 18),
            OrdersExportColumnCatalog.Columns[35]);
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
            OrdersExportColumnCatalog.Columns[36]);
        Assert.Equal(36, OrdersExportColumnCatalog.IndexOf("tax_rate"));
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
            OrdersExportColumnCatalog.Columns[37]);
        Assert.Equal(37, OrdersExportColumnCatalog.IndexOf("company_tax_id"));
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
            OrdersExportColumnCatalog.Columns[38]);
        Assert.Equal(38, OrdersExportColumnCatalog.IndexOf("coordinadora_city"));
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
            OrdersExportColumnCatalog.Columns.Skip(39).Take(OrdersExportColumnCatalog.PaymentDateColumns));
        Assert.Equal(43, OrdersExportColumnCatalog.IndexOf("payment_method_5"));
        Assert.Equal(34, OrdersExportColumnCatalog.IndexOf("bank_account"));
        Assert.Equal(
            ["coordinadora_city", "payment_method_1", "payment_method_2", "payment_method_3", "payment_method_4", "payment_method_5", "carrier",
                "freight_without_tax", "freight_tax", "freight_charge_mode", "cash_on_delivery", "variant",
                "customer_identification_type", "customer_person_type", "customer_address", "customer_city",
                "customer_department", "customer_phone", "customer_email",
                "billing_name", "billing_identification_type", "billing_identification_number", "billing_person_type",
                "billing_address", "billing_city", "billing_department", "billing_phone", "billing_email",
                "shipping_name", "shipping_identification_type", "shipping_identification_number", "shipping_person_type",
                "shipping_address", "shipping_city", "shipping_city_coordinadora", "shipping_department", "shipping_phone", "shipping_email"],
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
            OrdersExportColumnCatalog.Columns[44]);
        Assert.Equal(44, OrdersExportColumnCatalog.IndexOf("carrier"));
    }

    // Ajuste 2026-10-05: lo facturado del pedido (Quotation.Total), repetido en cada línea. Al final,
    // como toda columna nueva, para no mover lo que ya sale; visible por decisión del owner, así que
    // también la ganan los layouts guardados. Desde el ajuste 2026-10-06 la sigue "retention_amount".
    [Fact]
    public void OrderTotalIsAppendedAfterTheCarrierVisibleByDefault()
    {
        Assert.Equal(
            new OrdersExportCatalogColumn("order_total", "Total facturado", 18),
            OrdersExportColumnCatalog.Columns[45]);
        Assert.Equal(45, OrdersExportColumnCatalog.IndexOf("order_total"));
        Assert.Equal(35, OrdersExportColumnCatalog.IndexOf("proof_amount_total"));
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
            OrdersExportColumnCatalog.Columns[46]);
        Assert.Equal(46, OrdersExportColumnCatalog.IndexOf("retention_amount"));
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
        Assert.Equal(43, OrdersExportProcessor.Columns.Count);
    }

    // Review Focus 5: la llave se compara ordinal. "Company" no existe.
    [Fact]
    public void IndexOfIsOrdinal()
    {
        Assert.Equal(0, OrdersExportColumnCatalog.IndexOf("company"));
        Assert.Equal(32, OrdersExportColumnCatalog.IndexOf("unit_price_without_tax"));
        Assert.Equal(53, OrdersExportColumnCatalog.IndexOf("customer_name"));
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
