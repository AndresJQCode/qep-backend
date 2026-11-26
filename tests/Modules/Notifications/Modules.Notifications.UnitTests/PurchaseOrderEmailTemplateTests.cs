using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El correo de una orden de compra al proveedor: número, líneas, totales y el PDF adjunto. Todo llega ya formateado.</summary>
public sealed class PurchaseOrderEmailTemplateTests
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];

    private static PurchaseOrderEmailInput Input(string supplierName = "Textiles del Valle", string? notes = null) => new(
        "ventas@textiles.co",
        "Verde SAS",
        "OC-0007",
        "18/11/2026",
        supplierName,
        "COP",
        notes,
        [
            new PurchaseOrderEmailLine("CAM-M-AZ", "Camiseta básica – M / Azul", "24", "$ 36.900", "$ 885.600"),
            new PurchaseOrderEmailLine("PAN-32", "Pantalón talla 32", "10", "$ 80.000", "$ 800.000"),
        ],
        "$ 1.685.600",
        "$ 320.264",
        "$ 2.005.864",
        "OC-0007.pdf",
        Pdf);

    // Decisión del owner (spec 2026-11-19): una orden sin precio sale al proveedor sin la tabla de montos, no con una grilla de «—».
    private static PurchaseOrderEmailInput PricelessInput() => new(
        "ventas@textiles.co",
        "Verde SAS",
        "OC-0007",
        "18/11/2026",
        "Textiles del Valle",
        "COP",
        "Entregar en bodega norte",
        [
            new PurchaseOrderEmailLine("CAM-M-AZ", "Camiseta básica – M / Azul", "24", "—", "—"),
            new PurchaseOrderEmailLine("PAN-32", "Pantalón talla 32", "10", "—", "—"),
        ],
        "—",
        "—",
        "—",
        "OC-0007.pdf",
        Pdf,
        ShowsAmounts: false);

    [Fact]
    public void AnOrderWithoutAmountsKeepsQuantitiesAndProductsButNoAmountsSection()
    {
        var message = PurchaseOrderEmailTemplate.Render(PricelessInput());

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("CAM-M-AZ", body, StringComparison.Ordinal);
            Assert.Contains("PAN-32", body, StringComparison.Ordinal);
            Assert.Contains("24", body, StringComparison.Ordinal);
            Assert.Contains("10", body, StringComparison.Ordinal);
            Assert.Contains("Entregar en bodega norte", body, StringComparison.Ordinal);
            Assert.DoesNotContain("—", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Subtotal", body, StringComparison.Ordinal);
            Assert.DoesNotContain("IVA", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Total", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Costo", body, StringComparison.Ordinal);
        }

        Assert.Contains("Pantal&#243;n talla 32", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<th>Cantidad</th>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("- CAM-M-AZ · Camiseta básica – M / Azul: 24", message.TextBody, StringComparison.Ordinal);
        Assert.Equal("OC-0007.pdf", Assert.Single(message.Attachments!).FileName);
    }

    [Fact]
    public void AnOrderWithAmountsStillPrintsTheAmountsSection()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input());

        Assert.Contains("<th>Costo</th><th>Subtotal</th>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("IVA: $ 320.264", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubjectIsTheOrderNumberAndTheTenant()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input());

        Assert.Equal("ventas@textiles.co", message.ToAddress);
        Assert.Equal("Orden de compra OC-0007 de Verde SAS", message.Subject);
    }

    [Fact]
    public void ThePdfGoesAttachedAsApplicationPdf()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input());

        var attachment = Assert.Single(message.Attachments!);
        Assert.Equal("OC-0007.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(Pdf, attachment.Content.ToArray());
    }

    [Fact]
    public void BothBodiesListEveryLineAndTheTotals()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input(notes: "Entregar en bodega norte"));

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("OC-0007", body, StringComparison.Ordinal);
            Assert.Contains("Textiles del Valle", body, StringComparison.Ordinal);
            Assert.Contains("CAM-M-AZ", body, StringComparison.Ordinal);
            Assert.Contains("PAN-32", body, StringComparison.Ordinal);
            Assert.Contains("$ 885.600", body, StringComparison.Ordinal);
            Assert.Contains("$ 2.005.864", body, StringComparison.Ordinal);
            Assert.Contains("Entregar en bodega norte", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutNotesNoNotesLineIsPrinted()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input(notes: null));

        Assert.DoesNotContain("Notas", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Notas", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHtmlBodyEncodesSupplierNameAndNotes()
    {
        var message = PurchaseOrderEmailTemplate.Render(Input(supplierName: "Tela <b>&</b> Cia", notes: "<script>alert(1)</script>"));

        Assert.DoesNotContain("<script>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>&</b>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Tela &lt;b&gt;&amp;&lt;/b&gt; Cia", message.HtmlBody, StringComparison.Ordinal);
        // El texto plano no es HTML: va tal cual.
        Assert.Contains("<script>alert(1)</script>", message.TextBody, StringComparison.Ordinal);
    }
}
