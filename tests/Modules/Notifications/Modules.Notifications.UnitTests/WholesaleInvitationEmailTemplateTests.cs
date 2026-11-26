using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El correo de invitación de un mayorista a un distribuidor (plan mayorista fase 2, Task 8; copia O-3).</summary>
public sealed class WholesaleInvitationEmailTemplateTests
{
    private const string Link = "https://qep.qcode.co/wholesale-invitations/abc_DEF-1";

    // 14:30 UTC del 20 de noviembre son las 09:30 en Bogotá.
    private static readonly DateTimeOffset ExpiresAt = new(2026, 11, 20, 14, 30, 0, TimeSpan.Zero);

    private static readonly EmailRegionalFormat Colombian =
        EmailRegionalFormat.Colombian(TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    [Fact]
    public void SubjectNamesTheWholesaler()
    {
        var message = Render("Mayorista Central", "Distribuciones Norte SAS");

        Assert.Equal("ana@distri.co", message.ToAddress);
        Assert.Equal("Mayorista Central te invita a comprarle en Lulo CRM", message.Subject);
        Assert.Equal("wholesale-invitation", WholesaleInvitationEmailTemplate.TemplateRef);
    }

    [Fact]
    public void BodyCarriesTheLinkAndTheExpiryInTheTenantFormat()
    {
        var message = Render("Mayorista Central", "Distribuciones Norte SAS");

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Mayorista Central quiere venderle a Distribuciones Norte SAS por Lulo CRM.", body, StringComparison.Ordinal);
            Assert.Contains("Acepta la invitación para crear tus órdenes de compra con sus precios.", body, StringComparison.Ordinal);
            Assert.Contains("El enlace vence el 20/11/2026 09:30.", body, StringComparison.Ordinal);
            Assert.Contains(Link, body, StringComparison.Ordinal);
        }

        Assert.Contains($"<a href=\"{Link}\">Aceptar invitación</a>", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAreHtmlEscaped()
    {
        var message = Render("<b>Mayorista</b>", "Distri & <i>Norte</i>");

        Assert.Contains("&lt;b&gt;Mayorista&lt;/b&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Distri &amp; &lt;i&gt;Norte&lt;/i&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Mayorista</b>", message.HtmlBody, StringComparison.Ordinal);
        // El texto plano no tiene entidades: va crudo, como en InvitationEmailTemplate.
        Assert.Contains("<b>Mayorista</b> quiere venderle a Distri & <i>Norte</i>", message.TextBody, StringComparison.Ordinal);
    }

    // Ruling W1: el link sale de AppBaseUrl, sin variable nueva; el token va escapado.
    [Theory]
    [InlineData("https://qep.qcode.co", "abc_DEF-1", "https://qep.qcode.co/wholesale-invitations/abc_DEF-1")]
    [InlineData("http://localhost:3002/", "a+b/c=", "http://localhost:3002/wholesale-invitations/a%2Bb%2Fc%3D")]
    public void TheLinkIsTheAppBaseUrlAndTheEscapedToken(string appBaseUrl, string token, string expected) =>
        Assert.Equal(expected, AccountLinks.WholesaleInvitation(appBaseUrl, token));

    // Spec 2026-11-24: sólo el link del correo lleva la prueba de buzón, escapada; sin ella el link queda como antes.
    [Theory]
    [InlineData("https://qep.qcode.co", "abc", "p_Q-1", "https://qep.qcode.co/wholesale-invitations/abc?ev=p_Q-1")]
    [InlineData("http://localhost:3002/", "abc", "a+b/c=", "http://localhost:3002/wholesale-invitations/abc?ev=a%2Bb%2Fc%3D")]
    [InlineData("https://qep.qcode.co", "abc", null, "https://qep.qcode.co/wholesale-invitations/abc")]
    [InlineData("https://qep.qcode.co", "abc", "", "https://qep.qcode.co/wholesale-invitations/abc")]
    public void TheLinkCarriesTheEmailProofAsEvOnlyWhenThereIsOne(
        string appBaseUrl, string token, string? emailProof, string expected) =>
        Assert.Equal(expected, AccountLinks.WholesaleInvitation(appBaseUrl, token, emailProof));

    private static EmailMessage Render(string wholesalerName, string businessName) =>
        WholesaleInvitationEmailTemplate.Render("ana@distri.co", Link, wholesalerName, businessName, ExpiresAt, Colombian);
}
