using Microsoft.Extensions.Logging;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>
/// La apertura de un ticket recién confirmado (plan F23, F25 y F26), la misma para las tres altas: el formulario público, el manual y el
/// que nace de una conversación. Se llama <b>después</b> del commit: el ticket ya existe pase lo que pase con el envío. El resultado se
/// anota en un segundo guardado que toca sólo <c>ticket_access</c>, nunca el ticket ni su versión (el 201 sigue valiendo para el
/// siguiente <c>If-Match</c>). Si el envío lanza o la anotación falla, se loguea sin el PIN, el radicado ni el destinatario y la respuesta
/// sigue: lo único que se pierde es lo que la consola ve en <c>ConsultAccess</c>. Anotado el resultado, avisa
/// <c>supportTicketChanged</c> otra vez (revisión final S3): el aviso del alta salió antes de la anotación, y sin el segundo las otras
/// pantallas se quedan con el <c>ConsultAccess</c> de antes del envío.
/// </summary>
public sealed partial class SupportOpeningDelivery(
    SupportOpeningSender sender,
    ISupportUnitOfWork unitOfWork,
    ISupportRealtime realtime,
    ILogger<SupportOpeningDelivery> logger)
{
    // Sólo el id del ticket y el tipo de la excepción: nunca el PIN, el radicado ni el destinatario.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The opening of support ticket {TicketId} could not be sent ({ExceptionType}); it is recorded as send_failed.")]
    private static partial void LogSendThrew(ILogger logger, Guid ticketId, string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The opening delivery of support ticket {TicketId} could not be recorded ({ExceptionType}); the ticket was still created.")]
    private static partial void LogDeliveryNotRecorded(ILogger logger, Guid ticketId, string exceptionType);

    /// <summary>Null si el ticket no tiene a dónde (<see cref="SupportOpeningSender.HasDestination"/>): no se manda ni se anota nada.</summary>
    public async Task<SupportOpeningOutcome?> DeliverAsync(
        SupportTicket ticket, SupportTicketAccess access, string pin, string consultUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(access);
        if (!SupportOpeningSender.HasDestination(ticket))
        {
            return null;
        }

        var outcome = await SendAsync(ticket, pin, access.PinIssuedAt, consultUrl, cancellationToken);
        try
        {
            access.RecordDelivery(outcome.Channel, outcome.FailureCode, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDeliveryNotRecorded(logger, ticket.Id.Value, exception.GetType().Name);
            return outcome;
        }

        await realtime.TicketChangedAsync(ticket.TenantId, ticket.Id.Value, CancellationToken.None);
        return outcome;
    }

    /// <summary>El slug del tenant, para el enlace de consulta de un ticket que abre un asesor. Se lee antes de la transacción: después del
    /// commit nada puede lanzar. Un tenant con membresía activa siempre tiene su fila.</summary>
    public static async Task<string> SlugAsync(ITenantDirectory tenants, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        return await tenants.GetSlugAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no slug.");
    }

    /// <summary><see cref="SupportOpeningSender.SendAsync"/> sin excepciones: una que lance (el canal, la base de Mensajería) es
    /// <c>send_failed</c> por el canal del ticket. Lo usan la apertura y «Reenviar PIN», que responden igual pase lo que pase.</summary>
    public async Task<SupportOpeningOutcome> SendAsync(
        SupportTicket ticket, string pin, DateTimeOffset issuedAt, string consultUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        try
        {
            return await sender.SendAsync(ticket, pin, issuedAt, consultUrl, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendThrew(logger, ticket.Id.Value, exception.GetType().Name);
            return new SupportOpeningOutcome(SupportOpeningSender.ChannelOf(ticket), SupportChannelReasons.SendFailed, false);
        }
    }
}
