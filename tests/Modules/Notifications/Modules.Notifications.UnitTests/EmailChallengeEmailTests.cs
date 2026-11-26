using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §4: el código de confirmación del distribuidor llega por correo, en español
/// colombiano tuteando, con su vigencia. El destinatario viaja en el payload porque todavía no hay usuario.
/// </summary>
public sealed class EmailChallengeEmailTests
{
    [Fact]
    public void TheMailCarriesTheCodeAndItsValidityInBothBodies()
    {
        var message = EmailChallengeEmailTemplate.Render("a@x.com", "042137", TimeSpan.FromMinutes(10));

        Assert.Equal("a@x.com", message.ToAddress);
        Assert.Equal("Lulo CRM - Tu código para vincularte", message.Subject);
        Assert.Contains("042137", message.TextBody);
        Assert.Contains("042137", message.HtmlBody);
        Assert.Contains("10 minutos", message.TextBody);
        Assert.Contains("10 minutos", message.HtmlBody);
        Assert.Equal("identity.email-challenge.v1", EmailChallengeEmailTemplate.TemplateRef);
    }

    [Fact]
    public void TheWorkerReadsTheRecipientAndTheCodeFromThePayload()
    {
        var challengeId = Guid.CreateVersion7();
        var payload = EmailChallengePayload.Parse(
            $$"""{"challengeId":"{{challengeId}}","email":"a@x.com","code":"042137","expiresAt":"2026-11-19T12:10:00+00:00"}""");

        Assert.Equal(challengeId, payload.ChallengeId);
        Assert.Equal("a@x.com", payload.Email);
        Assert.Equal("042137", payload.Code);
    }

    [Fact]
    public void AMissingCodeOrEmailIsNullNotAnException()
    {
        var payload = EmailChallengePayload.Parse($$"""{"challengeId":"{{Guid.CreateVersion7()}}"}""");

        Assert.Null(payload.Email);
        Assert.Null(payload.Code);
    }
}
