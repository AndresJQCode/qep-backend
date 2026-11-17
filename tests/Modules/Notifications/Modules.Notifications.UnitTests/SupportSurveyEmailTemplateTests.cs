using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>Soporte fase 4b (H16, H19): el correo de la encuesta de satisfacción lleva el enlace de calificación. El enlace va escapado en el
/// HTML (un <c>&amp;</c> de la query no puede romper el atributo) y el texto tutea. Las aserciones sobre tildes miran el texto plano:
/// <c>WebUtility.HtmlEncode</c> escribe las tildes como entidades numéricas.</summary>
public sealed class SupportSurveyEmailTemplateTests
{
    [Fact]
    public void TheLinkIsEscapedAndTheTextTutea()
    {
        var url = new Uri("https://soporte.lulocrm.com/verde/calificar?token=Ab-c_1&origen=correo");

        var message = SupportSurveyEmailTemplate.Render(new SupportSurveyEmailInput(
            "ana@example.com", "Verde & Cia", "SOP-000127", "No me llego la <factura>", url));

        Assert.Equal("ana@example.com", message.ToAddress);
        Assert.Equal("¿Cómo te atendimos en tu caso SOP-000127?", message.Subject);
        Assert.Null(message.Attachments);

        Assert.Contains(
            "href=\"https://soporte.lulocrm.com/verde/calificar?token=Ab-c_1&amp;origen=correo\"", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("token=Ab-c_1&origen", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Verde &amp; Cia", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("No me llego la &lt;factura&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<factura>", message.HtmlBody, StringComparison.Ordinal);

        Assert.Contains(url.AbsoluteUri, message.TextBody, StringComparison.Ordinal);
        Assert.Contains("SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Califica la atención que recibiste", message.TextBody, StringComparison.Ordinal);
        foreach (var voseo in new[] { "calificá", "Calificá", "contanos", "Contanos", "tenés", "podés", "querés", "elegí" })
        {
            Assert.DoesNotContain(voseo, message.TextBody, StringComparison.Ordinal);
        }
    }
}
