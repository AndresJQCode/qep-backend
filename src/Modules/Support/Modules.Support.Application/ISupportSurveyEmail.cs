namespace Modules.Support.Application;

/// <summary>Fase 4b (H16, H19): la encuesta por correo, al correo de la foto del solicitante, con el enlace de calificación. El enlace lleva
/// el token: nunca va a un log.</summary>
public sealed record SupportSurveyEmail(string ToAddress, string TenantName, string TicketNumber, string TicketSubject, Uri RatingUrl);

/// <summary>El correo de la encuesta. El adaptador de Bootstrapper usa el canal y la plantilla de Notifications; cualquier falla del canal es
/// <see cref="SupportChannelSendResult.Failed"/> con <c>send_failed</c>, nunca una excepción.</summary>
public interface ISupportSurveyEmail
{
    Task<SupportChannelSendResult> SendAsync(SupportSurveyEmail email, CancellationToken cancellationToken);
}
