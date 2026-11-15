using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Cómo terminó el envío de la información del ticket con el PIN. <see cref="Channel"/> es el que queda en
/// <c>ticket_access.last_sent_channel</c>: <c>Email</c>, <c>Thread</c> o <c>None</c>; <see cref="FailureCode"/>, uno de
/// <see cref="SupportChannelReasons"/> si no salió.</summary>
public sealed record SupportOpeningOutcome(string Channel, string? FailureCode, bool Sent);

/// <summary>
/// El reparto por canal de la información del ticket con el PIN (plan F10, F23 y F25): el destino lo da el ticket registrado, nunca quien
/// lo pide. En este bloque sólo el correo: un ticket del formulario con solicitante, o uno manual con canal de respuesta <c>Email</c>, va
/// al correo de su foto. Un anónimo o uno sin destino no tiene a dónde (<see cref="HasDestination"/> en false): quien llama no genera PIN
/// ni escribe nada. El hilo de WhatsApp lo suma la Task 8.
/// </summary>
public sealed class SupportOpeningSender(ISupportOpeningEmail email, ITenantDirectory tenants)
{
    public const string EmailChannel = "Email";

    public const string ThreadChannel = "Thread";

    public const string NoChannel = "None";

    /// <summary>Si el ticket tiene a dónde recibir la información con el PIN.</summary>
    public static bool HasDestination(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return EmailOf(ticket) is not null;
    }

    public async Task<SupportOpeningOutcome> SendAsync(
        SupportTicket ticket, string pin, string consultUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (EmailOf(ticket) is not { } address)
        {
            return new SupportOpeningOutcome(NoChannel, null, false);
        }

        var tenantName = await tenants.GetDisplayNameAsync(new TenantId(ticket.TenantId), cancellationToken) ?? string.Empty;
        var result = await email.SendAsync(
            new SupportOpeningEmail(address, tenantName, ticket.DisplayNumber, ticket.TypeName, ticket.DueOn, consultUrl, pin),
            cancellationToken);
        return result switch
        {
            SupportChannelSendResult.Sent => new SupportOpeningOutcome(EmailChannel, null, true),
            SupportChannelSendResult.Rejected rejected => new SupportOpeningOutcome(EmailChannel, rejected.Reason, false),
            SupportChannelSendResult.Failed failed => new SupportOpeningOutcome(EmailChannel, failed.Code, false),
            _ => new SupportOpeningOutcome(EmailChannel, SupportChannelReasons.SendFailed, false),
        };
    }

    /// <summary>Plan F29: el enlace de consulta. Lleva el radicado, nunca el PIN.</summary>
    public static string ConsultUrl(ISupportPublicOptions options, string slug, string displayNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"{options.BaseUrl}/{slug}/consultar?radicado={Uri.EscapeDataString(displayNumber)}";
    }

    // El correo de la foto, si el ticket se responde por correo: el del formulario con solicitante (siempre por correo, F22) o el manual
    // con canal Email (F26). Uno manual por WhatsApp o desde una conversación no recibe correo aunque la foto lo traiga.
    private static string? EmailOf(SupportTicket ticket) =>
        !ticket.IsAnonymous
        && ticket.ReplyChannel is SupportReplyChannel.Email
        && ticket.Requester?.Email is { } address
        && !string.IsNullOrWhiteSpace(address)
            ? address.Trim()
            : null;
}
