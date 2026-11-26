using System.Globalization;
using System.Net;
using BuildingBlocks.Domain.Localization;

namespace Modules.Notifications.Application;

/// <summary>Lo que necesita el correo de apertura de un ticket: destinatario, el nombre del tenant, el radicado (<c>SOP-000127</c>), el
/// tipo, el vencimiento (null sin plazo), el enlace de consulta, el PIN y el idioma del tenant (spec 2026-11-22: quien abre el caso es
/// un cliente sin preferencia propia).</summary>
public sealed record SupportOpeningEmailInput(
    string ToAddress,
    string TenantName,
    string TicketNumber,
    string TypeName,
    DateOnly? DueOn,
    string ConsultUrl,
    string Pin,
    string Language = SupportedLanguages.Default);

/// <summary>
/// Soporte fase 3 (plan F23): el correo con la información del ticket recién radicado y su clave de consulta (PIN). El PIN va en el
/// cuerpo y nunca en el asunto, que se ve en la bandeja y en las notificaciones del teléfono; el enlace de consulta nunca lo lleva.
/// Tutea, como todo texto que el producto le manda a una persona.
/// </summary>
public static class SupportOpeningEmailTemplate
{
    public const string TemplateRef = "support.ticket-opened.v1";

    public static EmailMessage Render(SupportOpeningEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var subject = $"Recibimos tu caso {input.TicketNumber}";
        var due = input.DueOn is { } dueOn
            ? dueOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : "Sin fecha límite";
        // En España «radicado» no se usa: es «número de caso» (owner, 2026-11-22). El parámetro
        // `radicado` del enlace de consulta es contrato con el formulario público y no se traduce.
        var spain = SupportedLanguages.OrDefault(input.Language) == SupportedLanguages.SpainSpanish;
        var numberLabel = spain ? "Número de caso" : "Radicado";
        var withNumber = spain ? "Con el número de caso" : "Con el radicado";

        var textBody =
            $"Hola,\n\n" +
            $"{input.TenantName} recibió tu caso.\n\n" +
            $"{numberLabel}: {input.TicketNumber}\n" +
            $"Tipo: {input.TypeName}\n" +
            $"Fecha de respuesta: {due}\n" +
            $"Clave de consulta (PIN): {input.Pin}\n\n" +
            $"{withNumber} y esta clave puedes consultar el estado de tu caso y su respuesta aquí:\n" +
            $"{input.ConsultUrl}\n\n" +
            $"Guarda esta clave y no la compartas: quien la tenga puede ver tu caso.\n";

        var htmlBody =
            $"<p>Hola,</p>" +
            $"<p>{Html(input.TenantName)} recibió tu caso.</p>" +
            $"<p>{numberLabel}: <strong>{Html(input.TicketNumber)}</strong><br>" +
            $"Tipo: {Html(input.TypeName)}<br>" +
            $"Fecha de respuesta: {Html(due)}<br>" +
            $"Clave de consulta (PIN): <strong>{Html(input.Pin)}</strong></p>" +
            $"<p>{withNumber} y esta clave puedes consultar el estado de tu caso y su respuesta aquí: " +
            $"<a href=\"{Html(input.ConsultUrl)}\">{Html(input.ConsultUrl)}</a></p>" +
            $"<p>Guarda esta clave y no la compartas: quien la tenga puede ver tu caso.</p>";

        return new EmailMessage(input.ToAddress, subject, htmlBody, textBody);
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
