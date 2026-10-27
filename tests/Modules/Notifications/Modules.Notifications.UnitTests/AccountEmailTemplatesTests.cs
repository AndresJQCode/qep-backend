using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class AccountEmailTemplatesTests
{
    [Fact]
    public void TheVerificationMailCarriesTheVerifyLinkInBothBodies()
    {
        var url = AccountLinks.VerifyEmail("https://qep.qcode.co/", "abc_-1");
        Assert.Equal("https://qep.qcode.co/verify-email?token=abc_-1", url);
        var message = EmailVerificationEmailTemplate.Render("a@x.com", url);
        Assert.Contains(url, message.TextBody);
        Assert.Contains($"href=\"{url}\"", message.HtmlBody);
        Assert.Equal("Confirma tu correo", message.Subject);
    }

    [Fact]
    public void TheVerificationMailSaysTheLinkLastsTwentyFourHours()
        => Assert.Contains("24 horas", EmailVerificationEmailTemplate.Render("a@x.com", "https://q/verify-email?token=t").TextBody);

    [Fact]
    public void TheResetMailSaysTheLinkLastsOneHour()
        => Assert.Contains("1 hora", PasswordResetEmailTemplate.Render("a@x.com", "https://q/reset-password?token=t").TextBody);

    [Fact]
    public void TheResetLinkEscapesTheToken()
        => Assert.Equal(
            "https://q/reset-password?token=a%2Bb%2Fc%3D",
            AccountLinks.ResetPassword("https://q", "a+b/c="));

    [Fact]
    public void TheExistingAccountMailLinksToLoginAndForgotPassword()
    {
        var message = ExistingAccountEmailTemplate.Render("a@x.com", AccountLinks.Login("https://q"), AccountLinks.ForgotPassword("https://q"));
        Assert.Contains("https://q/login", message.TextBody);
        Assert.Contains("https://q/forgot-password", message.TextBody);
        Assert.Equal("Ya tienes una cuenta", message.Subject);
    }

    [Fact]
    public void TheAccountTemplateRefsAreVersionOne()
    {
        Assert.Equal("identity.email-verification.v1", EmailVerificationEmailTemplate.TemplateRef);
        Assert.Equal("identity.password-reset.v1", PasswordResetEmailTemplate.TemplateRef);
        Assert.Equal("identity.existing-account.v1", ExistingAccountEmailTemplate.TemplateRef);
    }
}
