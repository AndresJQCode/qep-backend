using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Cómo terminó el envío de la información del ticket con el PIN. <see cref="Channel"/> es el que queda en
/// <c>ticket_access.last_sent_channel</c>: <c>Email</c>, <c>Thread</c> o <c>None</c>; <see cref="FailureCode"/>, uno de
/// <see cref="SupportChannelReasons"/> si no salió.</summary>
public sealed record SupportOpeningOutcome(string Channel, string? FailureCode, bool Sent);

/// <summary>
/// El reparto por canal de la información del ticket con el PIN (plan F10, F11 y F23 a F26). El destino lo da el ticket registrado,
/// nunca quien lo pide:
/// <list type="bullet">
/// <item>Correo: el ticket que se responde por correo y tiene correo en la foto (el del formulario con solicitante, F22, o el manual con
/// canal <c>Email</c>, F26).</item>
/// <item>Hilo: cualquier otro que tenga conversación (el que nació de una, F25, o uno manual que ya la vinculó). Sólo texto, sólo con la
/// ventana de 24 h abierta: no hay plantilla de apertura (F24). Firma el envío quien creó el ticket, si sigue activo; si no, su
/// responsable; si tampoco, el primer gestor por nombre (F11). Sin ninguno no sale (<c>no_author</c>).</item>
/// <item>Nada: un anónimo, o un manual por WhatsApp o por el hilo sin conversación (F26): <see cref="HasDestination"/> en false.</item>
/// </list>
/// El texto lleva el enlace de consulta, que nunca lleva el PIN (F29).
/// </summary>
public sealed class SupportOpeningSender(
    ISupportOpeningEmail email,
    ISupportReplyChannels channels,
    ISupportMemberDirectory members,
    ISupportTicketRepository tickets,
    ITenantDirectory tenants)
{
    // Fase 4b (H17): la regla del autor es la misma de la encuesta; vive en SupportSystemAuthor.
    private readonly SupportSystemAuthor author = new(members, tickets);

    public const string EmailChannel = "Email";

    public const string ThreadChannel = "Thread";

    public const string NoChannel = "None";

    /// <summary>Lo que dice el texto cuando el tipo no tiene término.</summary>
    public const string NoDeadline = "sin fecha límite";

    /// <summary>Si el ticket tiene a dónde recibir la información con el PIN.</summary>
    public static bool HasDestination(SupportTicket ticket) => ChannelOf(ticket) != NoChannel;

    /// <summary>El canal por el que saldría la información: lo que se anota al reclamar un reenvío, antes de mandar.</summary>
    public static string ChannelOf(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.IsAnonymous)
        {
            return NoChannel;
        }

        if (EmailOf(ticket) is not null)
        {
            return EmailChannel;
        }

        return ticket.ConversationId is not null ? ThreadChannel : NoChannel;
    }

    /// <param name="issuedAt">Cuándo se emitió el PIN que se manda (<c>ticket_access.pin_issued_at</c>, o el instante del reenvío que lo
    /// va a emitir). Es también la hora con que se mira la ventana del hilo, y con el ticket arma el <c>ClientId</c> del mensaje: un
    /// reintento de la misma emisión no manda dos veces, y un PIN nuevo es otro mensaje.</param>
    public async Task<SupportOpeningOutcome> SendAsync(
        SupportTicket ticket, string pin, DateTimeOffset issuedAt, string consultUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return ChannelOf(ticket) switch
        {
            EmailChannel => await SendEmailAsync(ticket, EmailOf(ticket)!, pin, consultUrl, cancellationToken),
            ThreadChannel => await SendThreadAsync(ticket, pin, issuedAt, consultUrl, cancellationToken),
            _ => new SupportOpeningOutcome(NoChannel, null, false),
        };
    }

    /// <summary>Plan F29: el enlace de consulta. Lleva el radicado, nunca el PIN.</summary>
    public static string ConsultUrl(ISupportPublicOptions options, string slug, string displayNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"{options.BaseUrl}/{slug}/consultar?radicado={Uri.EscapeDataString(displayNumber)}";
    }

    /// <summary>El texto por el hilo, tuteando (plan Tarea 8). <paramref name="dueOn"/> va con el formato de fecha del tenant.</summary>
    public static string ThreadText(
        string displayNumber, string typeName, DateOnly? dueOn, string dateFormat, string consultUrl, string pin)
    {
        var deadline = dueOn is { } due
            ? due.ToString(RegionalDateFormat.Pattern(dateFormat), CultureInfo.InvariantCulture)
            : NoDeadline;
        return $"Registramos tu caso {displayNumber} ({typeName}). Fecha límite de respuesta: {deadline}. " +
            $"Consulta su estado en {consultUrl} con la clave {pin}.";
    }

    /// <summary>El <c>ClientId</c> del mensaje: el mismo para el mismo ticket y la misma emisión del PIN, y nunca el de una respuesta
    /// (esos son GUID v7 de su fila). La emisión se trunca a microsegundos, que es lo que guarda <c>pin_issued_at</c> (timestamptz): el
    /// instante en memoria y el que vuelve de la base dan el mismo id (triage #20 del Bloque F).</summary>
    public static Guid ClientIdFor(SupportTicketId ticketId, DateTimeOffset issuedAt)
    {
        var microsecondTicks = issuedAt.UtcTicks - (issuedAt.UtcTicks % TimeSpan.TicksPerMicrosecond);
        var seed = Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"support.opening:{ticketId.Value:N}:{microsecondTicks}"));
        var bytes = SHA256.HashData(seed).AsSpan(0, 16).ToArray();
        // Versión 8 (definida por la aplicación) y la variante RFC 9562, en el orden de bytes de System.Guid.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private async Task<SupportOpeningOutcome> SendEmailAsync(
        SupportTicket ticket, string address, string pin, string consultUrl, CancellationToken cancellationToken)
    {
        var tenantName = await tenants.GetDisplayNameAsync(new TenantId(ticket.TenantId), cancellationToken) ?? string.Empty;
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(ticket.TenantId), cancellationToken);
        var language = BuildingBlocks.Domain.Localization.SupportedLanguages.OrDefault(regional?.Language);
        var result = await email.SendAsync(
            new SupportOpeningEmail(
                address, tenantName, ticket.DisplayNumber, ticket.TypeName, ticket.DueOn, consultUrl, pin, language),
            cancellationToken);
        return Outcome(EmailChannel, result);
    }

    private async Task<SupportOpeningOutcome> SendThreadAsync(
        SupportTicket ticket, string pin, DateTimeOffset issuedAt, string consultUrl, CancellationToken cancellationToken)
    {
        var conversationId = ticket.ConversationId!.Value;
        var thread = await channels.FindThreadAsync(ticket.TenantId, conversationId, cancellationToken);
        var unavailable = thread switch
        {
            null => SupportChannelReasons.ConversationNotFound,
            { Blocked: true } => SupportChannelReasons.ContactBlocked,
            { ConnectionOperational: false } => SupportChannelReasons.ConnectionUnavailable,
            // Fuera de la ventana no sale nada: la apertura no tiene plantilla (plan F24 y F25).
            { WindowClosesAt: null } => SupportChannelReasons.WindowClosed,
            { WindowClosesAt: { } closesAt } when closesAt <= issuedAt => SupportChannelReasons.WindowClosed,
            _ => null,
        };
        if (unavailable is not null)
        {
            return new SupportOpeningOutcome(ThreadChannel, unavailable, false);
        }

        if (await this.author.AuthorAsync(ticket, cancellationToken) is not { } author)
        {
            return new SupportOpeningOutcome(ThreadChannel, SupportChannelReasons.NoAuthor, false);
        }

        var dateFormat = (await tenants.GetRegionalSettingsAsync(new TenantId(ticket.TenantId), cancellationToken))?.DateFormat;
        var text = ThreadText(ticket.DisplayNumber, ticket.TypeName, ticket.DueOn, dateFormat ?? string.Empty, consultUrl, pin);
        var result = await channels.SendThreadAsync(
            new SupportChannelThreadSend(
                ticket.TenantId, conversationId, ClientIdFor(ticket.Id, issuedAt), author, text, UploadId: null),
            cancellationToken);
        return Outcome(ThreadChannel, result);
    }

    private static SupportOpeningOutcome Outcome(string channel, SupportChannelSendResult result) => result switch
    {
        SupportChannelSendResult.Sent => new SupportOpeningOutcome(channel, null, true),
        SupportChannelSendResult.Rejected rejected => new SupportOpeningOutcome(channel, rejected.Reason, false),
        SupportChannelSendResult.Failed failed => new SupportOpeningOutcome(channel, failed.Code, false),
        _ => new SupportOpeningOutcome(channel, SupportChannelReasons.SendFailed, false),
    };

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
