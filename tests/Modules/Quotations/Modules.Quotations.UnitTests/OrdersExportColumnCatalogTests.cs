using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El catálogo del Excel de pedidos (spec 2026-09-24): las 35 columnas de hoy, con la llave estable
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
        "unit_price_without_tax", "order_date", "customer_name",
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
        "Valor Unit sin IVA", "Fecha Pedido", "Cliente",
    ];

    [Fact]
    public void HasTheThirtyFiveColumnsOfTheSpecInItsOrder()
    {
        Assert.Equal(35, OrdersExportColumnCatalog.Columns.Count);
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
    [Fact]
    public void MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth()
    {
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => new ExportColumn(column.DefaultHeader, column.Width)),
            OrdersExportProcessor.Columns);
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
