namespace Modules.Support.Application;

/// <summary>El correo de apertura de un ticket (plan F23): al correo de la foto del solicitante, con el radicado, el tipo, el vencimiento,
/// el enlace de consulta y el PIN. El enlace nunca lleva el PIN.</summary>
/// <param name="Language">El idioma del tenant (spec 2026-11-22, i18n): quien abre el caso es un cliente sin preferencia propia.</param>
public sealed record SupportOpeningEmail(
    string ToAddress,
    string TenantName,
    string TicketNumber,
    string TypeName,
    DateOnly? DueOn,
    string ConsultUrl,
    string Pin,
    string Language = BuildingBlocks.Domain.Localization.SupportedLanguages.Default);

/// <summary>
/// El correo de apertura. El adaptador de Bootstrapper usa el canal y la plantilla de Notifications (calco de <c>ISupportReplyEmail</c>):
/// cualquier falla del canal es <see cref="SupportChannelSendResult.Failed"/> con <c>send_failed</c>, nunca una excepción. Se manda
/// después de confirmar el ticket: el radicado no depende del correo.
/// </summary>
public interface ISupportOpeningEmail
{
    Task<SupportChannelSendResult> SendAsync(SupportOpeningEmail email, CancellationToken cancellationToken);
}
