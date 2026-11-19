using System.Net;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-19 §6: el aviso de que el correo de un distribuidor cambió al aceptar la invitación de un mayorista. Va a las
/// dos direcciones, al anterior y al nuevo, con el mismo asunto y un cuerpo para cada uno. Variables en allowlist: destinatario,
/// nombre del mayorista, razón social y los dos correos. Sin token ni enlace: el aviso sólo cuenta qué pasó y a quién acudir.
/// La copia es criterio del constructor (spec, «Lo que queda al constructor»); el owner la puede ajustar subiendo la referencia.
/// </summary>
public static class WholesaleEmailChangedEmailTemplate
{
    public const string TemplateRef = "wholesale-distributor-email-changed";

    public static EmailMessage Render(
        string recipient,
        bool toPreviousAddress,
        string wholesalerName,
        string businessName,
        string previousEmail,
        string newEmail)
    {
        var subject = $"Cambió el correo de {businessName} con {wholesalerName}";

        // Nombres y correos los escribe un usuario: en el HTML van escapados, en el texto plano crudos.
        var lead = toPreviousAddress
            ? (Text: $"Al aceptar la invitación de {wholesalerName}, el correo de contacto de {businessName} cambió de " +
                    $"{previousEmail} a {newEmail}. Desde ahora el correo de contacto es {newEmail}.",
               Html: $"Al aceptar la invitación de {Encode(wholesalerName)}, el correo de contacto de {Encode(businessName)} " +
                    $"cambió de {Encode(previousEmail)} a {Encode(newEmail)}. Desde ahora el correo de contacto es " +
                    $"{Encode(newEmail)}.")
            : (Text: $"Aceptaste la invitación de {wholesalerName} y el correo de contacto de {businessName} quedó como " +
                    $"{newEmail}, en lugar de {previousEmail}.",
               Html: $"Aceptaste la invitación de {Encode(wholesalerName)} y el correo de contacto de {Encode(businessName)} " +
                    $"quedó como {Encode(newEmail)}, en lugar de {Encode(previousEmail)}.");
        var closing = $"Si no reconoces este cambio, escríbele a {wholesalerName}.";

        var textBody = $"Hola.\n\n{lead.Text}\n\n{closing}\n";
        var htmlBody =
            $"<p>Hola.</p>" +
            $"<p>{lead.Html}</p>" +
            $"<p>Si no reconoces este cambio, escríbele a {Encode(wholesalerName)}.</p>";

        return new EmailMessage(recipient, subject, htmlBody, textBody);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
