using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Soporte fase 3 (plan F23): el correo de apertura con el radicado, el PIN y el enlace de consulta. El enlace nunca lleva el PIN.
/// </summary>
public sealed class SupportOpeningEmailTemplateTests
{
    private static SupportOpeningEmailInput Input(string tenantName = "Verde Cia", DateOnly? dueOn = null) => new(
        "ana@example.com",
        tenantName,
        "SOP-000127",
        "Petición",
        dueOn,
        "https://soporte.lulocrm.com/verde/consultar?radicado=SOP-000127",
        "048213");

    [Fact]
    public void ItCarriesTheNumberThePinAndTheLink()
    {
        var message = SupportOpeningEmailTemplate.Render(Input(dueOn: new DateOnly(2026, 11, 27)));

        Assert.Equal("ana@example.com", message.ToAddress);
        Assert.Contains("SOP-000127", message.Subject, StringComparison.Ordinal);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("SOP-000127", body, StringComparison.Ordinal);
            Assert.Contains("048213", body, StringComparison.Ordinal);
            Assert.Contains("https://soporte.lulocrm.com/verde/consultar?radicado=SOP-000127", body, StringComparison.Ordinal);
            Assert.Contains("27/11/2026", body, StringComparison.Ordinal);
        }

        // El PIN va en el cuerpo, nunca en el asunto (que se ve en la bandeja y en las notificaciones del teléfono).
        Assert.DoesNotContain("048213", message.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void ItHtmlEncodesTheTenantName()
    {
        var message = SupportOpeningEmailTemplate.Render(Input(tenantName: "Verde & <Cia>"));

        Assert.Contains("Verde &amp; &lt;Cia&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Cia>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Verde & <Cia>", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTermItSaysSinFechaLimite()
    {
        var message = SupportOpeningEmailTemplate.Render(Input(dueOn: null));

        Assert.Contains("Sin fecha límite", message.TextBody, StringComparison.Ordinal);
    }

    // Tutea en colombiano (regla del repo): ni voseo ni usted.
    [Fact]
    public void TheTextTutea()
    {
        var text = SupportOpeningEmailTemplate.Render(Input(dueOn: new DateOnly(2026, 11, 27))).TextBody;

        Assert.Contains("tu caso", text, StringComparison.Ordinal);
        Assert.Contains("puedes", text, StringComparison.Ordinal);
        foreach (var voseo in new[] { "podés", "tenés", "consultá", "ingresá", "guardá", "usted", "su caso" })
        {
            Assert.DoesNotContain(voseo, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
