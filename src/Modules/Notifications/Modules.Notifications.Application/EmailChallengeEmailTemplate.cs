using System.Globalization;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-19 (onboarding por link): el código con el que el distribuidor confirma su correo antes de vincularse a un
/// mayorista. No nombra ningún tenant: el correo todavía no es de un usuario. El código va en texto, nunca en un enlace.
/// </summary>
public static class EmailChallengeEmailTemplate
{
    public const string TemplateRef = "identity.email-challenge.v1";

    public static EmailMessage Render(string recipientAddress, string code, TimeSpan validity)
    {
        const string subject = "Lulo CRM - Tu código para vincularte";
        var minutes = ((int)Math.Round(validity.TotalMinutes)).ToString(CultureInfo.InvariantCulture);

        var textBody =
            "Hola,\n\n" +
            "Para confirmar tu correo y vincularte, escribe este código:\n\n" +
            $"{code}\n\n" +
            $"El código vence en {minutes} minutos y sólo sirve una vez.\n" +
            "Si no lo pediste, puedes ignorar este mensaje.\n";

        var htmlBody =
            "<p>Hola,</p>" +
            "<p>Para confirmar tu correo y vincularte, escribe este código:</p>" +
            $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:4px\">{code}</p>" +
            $"<p>El código vence en {minutes} minutos y sólo sirve una vez.</p>" +
            "<p>Si no lo pediste, puedes ignorar este mensaje.</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
