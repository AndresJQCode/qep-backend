using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>Soporte fase 2 (plan E15): la respuesta de un ticket por correo. El texto lo escribe el asesor, así que en HTML va
/// escapado y con sus saltos de línea; en texto plano, tal cual. Los textos de la prueba van sin tildes a propósito:
/// <c>WebUtility.HtmlEncode</c> escribe las tildes como entidades numéricas, y la prueba mira el escape de <c>&lt;</c> y <c>&amp;</c>.</summary>
public sealed class SupportReplyEmailTemplateTests
{
    [Fact]
    public void TheEmailEscapesHtmlAndKeepsLineBreaks()
    {
        var message = SupportReplyEmailTemplate.Render(new SupportReplyEmailInput(
            "ana@example.com",
            "Verde & Cia",
            "SOP-000127",
            "No me llego la <factura>",
            "Hola Ana,\nya revisamos tu caso.\r\nTe enviamos la factura <hoy> & la confirmas pronto."));

        Assert.Equal("ana@example.com", message.ToAddress);
        Assert.Equal("Respuesta a tu caso SOP-000127", message.Subject);

        Assert.Contains(
            "Hola Ana,<br>ya revisamos tu caso.<br>Te enviamos la factura &lt;hoy&gt; &amp; la confirmas pronto.",
            message.HtmlBody,
            StringComparison.Ordinal);
        Assert.Contains("No me llego la &lt;factura&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Verde &amp; Cia", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<hoy>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<factura>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message.HtmlBody, StringComparison.Ordinal);

        Assert.Contains(
            "Hola Ana,\nya revisamos tu caso.\nTe enviamos la factura <hoy> & la confirmas pronto.",
            message.TextBody,
            StringComparison.Ordinal);
        Assert.Contains("SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Verde & Cia", message.TextBody, StringComparison.Ordinal);
    }

    /// <summary>Soporte fase 4b (H10): la respuesta formal por correo avisa que el PDF va adjunto; una respuesta normal no lo dice.</summary>
    [Fact]
    public void AFormalReplyMentionsTheAttachedPdf()
    {
        var input = new SupportReplyEmailInput("ana@example.com", "Verde", "SOP-000127", "Factura", "Ya revisamos tu caso.");

        var formal = SupportReplyEmailTemplate.Render(input with { HasDocument = true });
        var plain = SupportReplyEmailTemplate.Render(input);

        Assert.Contains("Adjuntamos la respuesta formal de tu caso en PDF.", formal.TextBody, StringComparison.Ordinal);
        Assert.Contains("<p>Adjuntamos la respuesta formal de tu caso en PDF.</p>", formal.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Adjuntamos", plain.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Adjuntamos", plain.HtmlBody, StringComparison.Ordinal);
        Assert.Null(formal.Attachments);
    }
}
