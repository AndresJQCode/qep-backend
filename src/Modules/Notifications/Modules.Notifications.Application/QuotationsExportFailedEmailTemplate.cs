namespace Modules.Notifications.Application;

/// <summary>
/// El correo de exportación fallida (D12). Sin el motivo técnico: `last_error` es para soporte y
/// a quien exporta no le sirve para nada que pueda hacer. Lo que sí le sirve es saber que no le
/// va a llegar el archivo y que puede pedirlo de nuevo.
/// </summary>
public static class QuotationsExportFailedEmailTemplate
{
    public const string TemplateRef = "quotations.export-failed.v1";

    public static EmailMessage Render(string recipientAddress, string kind)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"No pudimos generar tu exportación de {names.Plural}";
        var sentence = $"No pudimos generar tu exportación de {names.Plural}. Intenta de nuevo.";

        var textBody = $"Hola,\n\n{sentence}\n";
        var htmlBody = $"<p>Hola,</p><p>{sentence}</p>";

        return new EmailMessage(recipientAddress, subject, htmlBody, textBody);
    }
}
