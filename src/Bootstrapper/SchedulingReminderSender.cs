using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Modules.Messaging.Application;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-21, Tarea 12: el adaptador de <see cref="IAppointmentReminderSender"/> hacia Mensajería, calcado de
/// <see cref="WholesaleInvitationNoticeSender"/> y de <see cref="SupportReplyChannels"/>. Llena las cinco variables propias de la plantilla
/// (<c>nombre_cliente</c>, <c>servicio</c>, <c>fecha_hora</c>, <c>recurso</c> y <c>sede</c>; las cuatro fijas las pone Mensajería), escribe la
/// fecha y la hora en la zona del tenant, usa la plantilla vigente de la conexión (puede ser <c>_v2</c>: el nombre sale de la fila) y traduce
/// el resultado a los códigos que el barredor guarda.
/// <para>
/// La llave de idempotencia de la cita (<c>appointment-reminder:{id}</c>) es texto y el <c>clientId</c> de Mensajería es un <c>Guid</c>: se
/// deriva uno estable de la llave (<see cref="ClientIdOf"/>). Así, si el mensaje salió y marcar la cita falló, el reintento devuelve la misma
/// fila de Mensajería en vez de mandar un segundo recordatorio.
/// </para>
/// </summary>
internal sealed partial class SchedulingReminderSender(
    IAppointmentReminderOutbound outbound, IAppointmentReminderReadiness readiness, ITenantClock tenantClock) : IAppointmentReminderSender
{
    // El texto largo de la fecha va en español como el de la vigencia de las cotizaciones (la plantilla es `es`), con la hora en 24 h para no
    // depender del designador a. m./p. m. de cada versión de ICU.
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CO");

    // Meta rechaza un parámetro de más de 1024 caracteres (TemplateParameterRules.MaxValueLength).
    private const int MaxValueLength = TemplateParameterRules.MaxValueLength;

    public async Task<ReminderDelivery> SendAsync(ReminderMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // La vigente puede ser `_v2`: el nombre sale de la fila, no de la constante.
        if (await readiness.FindApprovedAsync(message.TenantId, message.ConnectionId, cancellationToken) is not { } template)
        {
            return new ReminderDelivery.Failed("template_not_approved");
        }

        var calendar = await tenantClock.GetAsync(message.TenantId, cancellationToken);
        var customerName = OneLine(message.CustomerName);

        var result = await outbound.SendAsync(
            new AppointmentReminderSend(
                message.TenantId,
                message.ConnectionId,
                message.SentByMemberId,
                ClientIdOf(message.IdempotencyKey),
                WaIdOf(message.PhoneE164),
                message.CustomerId,
                customerName,
                template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AppointmentReminderTemplateDefinition.ClientKey] = customerName,
                    [AppointmentReminderTemplateDefinition.ServiceKey] = OneLine(message.ServiceName),
                    [AppointmentReminderTemplateDefinition.DateTimeKey] = calendar.ToLocal(message.StartsAt).ToString("dddd d 'de' MMMM 'a las' HH:mm", Spanish),
                    [AppointmentReminderTemplateDefinition.ResourceKey] = OneLine(message.ResourceName),
                    [AppointmentReminderTemplateDefinition.LocationKey] = OneLine(message.LocationName),
                }),
            cancellationToken);

        return result switch
        {
            AppointmentReminderSendResult.Sent => new ReminderDelivery.Sent(),
            AppointmentReminderSendResult.ConnectionUnavailable unavailable =>
                new ReminderDelivery.Failed($"connection_unavailable:{unavailable.Reason ?? "unknown"}"),
            AppointmentReminderSendResult.Failed failed => new ReminderDelivery.Failed(failed.Code),
            _ => new ReminderDelivery.Failed("unknown"),
        };
    }

    /// <summary>Un <c>Guid</c> estable para una llave de texto: los primeros 16 bytes de su SHA-256. Dos llaves distintas no chocan en la
    /// práctica y la misma llave da siempre el mismo valor, que es lo que la idempotencia por <c>(conversation_id, client_id)</c> necesita.</summary>
    internal static Guid ClientIdOf(string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)).AsSpan(0, 16));
    }

    // El `wa_id` de Meta son sólo dígitos con indicativo; el teléfono guardado es E.164 («+573001234567»).
    private static string WaIdOf(string phoneE164) => new(phoneE164.Where(char.IsAsciiDigit).ToArray());

    // Meta rechaza saltos de línea, tabulaciones y más de 4 espacios seguidos: una línea, espacios simples.
    private static string OneLine(string? value)
    {
        var line = value is null ? string.Empty : Whitespace().Replace(value, " ").Trim();
        return line.Length <= MaxValueLength ? line : line[..MaxValueLength];
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
