using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class BuyerEmailTemplatesTests
{
    [Fact]
    public void TheVerificationEmailNamesTheStoreAndLinksToItsHost()
    {
        var url = BuyerAccountLinks.Verify("acme.lulocrm.com", "abc");
        var message = BuyerVerificationEmailTemplate.Render("ana@example.com", "Acme", url);

        Assert.Equal("https://acme.lulocrm.com/cuenta/verificar?token=abc", url);
        Assert.Equal("ana@example.com", message.ToAddress);
        Assert.Equal("Confirma tu cuenta para comprar en Acme", message.Subject);
        Assert.Contains("https://acme.lulocrm.com/cuenta/verificar?token=abc", message.HtmlBody);
        Assert.Contains("https://acme.lulocrm.com/cuenta/verificar?token=abc", message.TextBody);
        Assert.Contains("El enlace vence en 24 horas.", message.TextBody);
        Assert.Contains("Si no creaste una cuenta, puedes ignorar este mensaje.", message.TextBody);
        Assert.Equal("buyers.verification.v1", BuyerVerificationEmailTemplate.TemplateRef);
    }

    [Fact]
    public void TheExistingAccountEmailLinksToLoginAndForgot()
    {
        var message = BuyerExistingAccountEmailTemplate.Render(
            "ana@example.com",
            "Acme",
            BuyerAccountLinks.Login("acme.lulocrm.com"),
            BuyerAccountLinks.Forgot("acme.lulocrm.com"));

        Assert.Equal("Ya tienes una cuenta para comprar en Acme", message.Subject);
        Assert.Contains("https://acme.lulocrm.com/cuenta/ingresar", message.TextBody);
        Assert.Contains("https://acme.lulocrm.com/cuenta/olvide", message.TextBody);
        Assert.Contains("https://acme.lulocrm.com/cuenta/ingresar", message.HtmlBody);
        Assert.Contains("https://acme.lulocrm.com/cuenta/olvide", message.HtmlBody);
        Assert.Contains("La próxima vez inicia sesión con tu correo y tu contraseña.", message.TextBody);
        Assert.Equal("buyers.existing-account.v1", BuyerExistingAccountEmailTemplate.TemplateRef);
    }

    [Fact]
    public void TheResetEmailSaysOneHour()
    {
        var url = BuyerAccountLinks.Reset("acme.lulocrm.com", "tok");
        var message = BuyerPasswordResetEmailTemplate.Render("ana@example.com", "Acme", url);

        Assert.Equal("Restablece tu contraseña", message.Subject);
        Assert.Equal("https://acme.lulocrm.com/cuenta/restablecer?token=tok", url);
        Assert.Contains("El enlace vence en 1 hora.", message.TextBody);
        Assert.Contains("Si no lo pediste, puedes ignorar este mensaje.", message.TextBody);
        Assert.Contains("Acme", message.TextBody);
        Assert.Contains(url, message.HtmlBody);
        Assert.Equal("buyers.password-reset.v1", BuyerPasswordResetEmailTemplate.TemplateRef);
    }

    [Fact]
    public void TheDeletedEmailHasNoLink()
    {
        var message = BuyerAccountDeletedEmailTemplate.Render("ana@example.com", "Acme");

        Assert.Equal("Eliminamos tu cuenta", message.Subject);
        Assert.Contains(
            "Borramos tu cuenta, tus datos guardados y tus sesiones. Los pedidos que ya hiciste en cada tienda no cambian.",
            message.TextBody);
        Assert.DoesNotContain("http", message.TextBody);
        Assert.DoesNotContain("http", message.HtmlBody);
        Assert.Equal("buyers.account-deleted.v1", BuyerAccountDeletedEmailTemplate.TemplateRef);
    }

    [Fact]
    public void TheStoreNameIsHtmlEncoded()
    {
        const string storeName = "A&B <Tienda> \"x\"";
        const string url = "https://acme.lulocrm.com/cuenta/verificar?token=abc";

        var verification = BuyerVerificationEmailTemplate.Render("ana@example.com", storeName, url);
        var existing = BuyerExistingAccountEmailTemplate.Render("ana@example.com", storeName, "https://a/l", "https://a/f");
        var reset = BuyerPasswordResetEmailTemplate.Render("ana@example.com", storeName, url);
        var deleted = BuyerAccountDeletedEmailTemplate.Render("ana@example.com", storeName);

        foreach (var message in new[] { verification, existing, reset, deleted })
        {
            Assert.Contains("A&amp;B &lt;Tienda&gt; &quot;x&quot;", message.HtmlBody);
            Assert.DoesNotContain("<Tienda>", message.HtmlBody);
            Assert.Contains(storeName, message.TextBody);
        }

        Assert.Equal($"Confirma tu cuenta para comprar en {storeName}", verification.Subject);
        Assert.Equal($"Ya tienes una cuenta para comprar en {storeName}", existing.Subject);
    }

    [Fact]
    public void TheTokenIsEscapedInTheLink()
    {
        Assert.Equal(
            "https://acme.lulocrm.com/cuenta/verificar?token=a%2Bb%2Fc%3D",
            BuyerAccountLinks.Verify("acme.lulocrm.com", "a+b/c="));
        Assert.Equal(
            "https://acme.lulocrm.com/cuenta/restablecer?token=a%2Bb%2Fc%3D",
            BuyerAccountLinks.Reset("acme.lulocrm.com", "a+b/c="));
    }
}
