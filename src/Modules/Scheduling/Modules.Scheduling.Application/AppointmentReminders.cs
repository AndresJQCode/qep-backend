namespace Modules.Scheduling.Application;

/// <summary>
/// Plan 2026-11-21, Tarea 12: lo fijo del recordatorio de cita por WhatsApp. El barredor reclama cada cita con un <c>UPDATE</c> (varios pods
/// pueden correrlo a la vez: gana uno) y un reintento espera el reclamo del intento anterior, que crece con cada fallo hasta el tope.
/// </summary>
public static class AppointmentReminderRules
{
    /// <summary>Citas que una pasada del barredor toma por lote.</summary>
    public const int BatchSize = 50;

    /// <summary>Intentos de envío antes de rendirse: el tercer fallo deja la cita omitida con <c>send_failed:{código}</c>.</summary>
    public const int MaxAttempts = 3;

    /// <summary>La espera después del intento n es <c>Leases[n - 1]</c>. Es también el tiempo que tiene el envío para terminar antes de que
    /// otro pod lo reclame; el <c>clientId</c> idempotente evita el doble envío si lo hiciera.</summary>
    public static readonly IReadOnlyList<TimeSpan> Leases = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    /// <summary>
    /// La llave de idempotencia del envío (plan, Tarea 12 y corrección 49): el mismo valor para la misma cita <b>a la misma hora</b>. Lleva el
    /// inicio en ticks UTC (estable, sin zona) porque reprogramar la cita limpia su recordatorio y la nueva hora debe avisarse de nuevo: con la
    /// llave de antes, Mensajería deduplicaría ese segundo envío contra el primero.
    /// </summary>
    public static string KeyOf(Guid appointmentId, DateTimeOffset startsAt) => $"appointment-reminder:{appointmentId:D}:{startsAt.UtcTicks}";

    /// <summary>Una dirección que WhatsApp acepta: E.164 con «+», de 8 a 15 dígitos y sin ceros a la izquierda. Customers ya guarda
    /// <c>phone_e164</c> normalizado; esto sólo cierra el paso a lo que no lo está.</summary>
    public static bool IsDialable(string? phone) =>
        phone is { Length: >= 9 and <= 16 } && phone[0] == '+' && phone[1] != '0' && phone.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}

/// <summary>Por qué un recordatorio no sale. Quedan en la cita (<c>reminder_skipped_reason</c>) y en su historial: son texto de contrato para
/// la tarjeta «Historial», no se traducen.</summary>
public static class ReminderSkipReasons
{
    public const string NoPhone = "no_phone";

    public const string ConsentRevoked = "consent_revoked";

    public const string MarketingOptOut = "marketing_opt_out";

    public const string CustomerMissing = "customer_missing";

    public const string CustomerInactive = "customer_inactive";

    /// <summary>Ni el miembro del recurso ni quien tocó la cita por última vez siguen activos: la fila de Mensajería exige un autor.</summary>
    public const string NoSenderMember = "no_sender_member";

    /// <summary>El último fallo del envío, después de agotar los intentos: <c>send_failed:{código}</c>.</summary>
    public const string SendFailedPrefix = "send_failed:";
}

/// <summary>
/// Lo que el adaptador de Mensajería necesita para armar el recordatorio. <c>IdempotencyKey</c> es <see cref="AppointmentReminderRules.KeyOf"/>:
/// el adaptador la vuelve el <c>clientId</c> del mensaje. <c>SentByMemberId</c> firma el mensaje (Mensajería exige un autor). <c>StartsAt</c>
/// va en UTC: el adaptador la escribe en la zona del tenant.
/// </summary>
public sealed record ReminderMessage(
    Guid TenantId,
    Guid AppointmentId,
    string IdempotencyKey,
    Guid ConnectionId,
    Guid SentByMemberId,
    Guid CustomerId,
    string CustomerName,
    string PhoneE164,
    string ServiceName,
    string ResourceName,
    string LocationName,
    DateTimeOffset StartsAt);

public abstract record ReminderDelivery
{
    /// <summary>Salió, o ya había salido con esa llave.</summary>
    public sealed record Sent : ReminderDelivery;

    /// <summary>No salió. <paramref name="Code"/> es un código corto (<c>template_not_approved</c>, <c>connection_unavailable:paused</c>,
    /// <c>messaging.message.rejected</c>…) que el barredor guarda y reintenta.</summary>
    public sealed record Failed(string Code) : ReminderDelivery;
}

/// <summary>
/// Puerto hacia Mensajería (plan, corrección 1 de la Tarea 12): la Agenda no referencia Mensajería, así que el recordatorio sale por acá y el
/// adaptador de Bootstrapper lo manda con la plantilla aprobada de la conexión.
/// </summary>
public interface IAppointmentReminderSender
{
    Task<ReminderDelivery> SendAsync(ReminderMessage message, CancellationToken cancellationToken);
}

/// <summary>El cliente de una cita tal como el recordatorio lo necesita. <c>ConsentRevoked</c> es que la última fila de consentimiento de datos
/// es una revocación (cualquiera: también la que llegó por otro medio); <c>MarketingOptOut</c> es el de la ficha.</summary>
public sealed record ReminderRecipient(
    Guid CustomerId, string Name, string? PhoneE164, bool IsActive, bool ConsentRevoked, bool MarketingOptOut);

/// <summary>
/// Puerto hacia Clientes para el recordatorio (PD-7): teléfono, estado y qué dice su consentimiento. El adaptador vive en Bootstrapper y toda
/// lectura va acotada al tenant: un cliente de otro tenant no existe.
/// </summary>
public interface ISchedulingReminderRecipients
{
    Task<ReminderRecipient?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}

/// <summary>El estado de «no hay plantilla de recordatorio» (spec 2026-11-23 §2): lo que responde el puerto sin fila o sin Mensajería.</summary>
public static class ReminderTemplateStates
{
    public const string MissingStatus = "Missing";

    public static ReminderTemplateStateDto Missing { get; } = new(MissingStatus, null);
}

/// <summary>
/// Puerto hacia Mensajería para los ajustes (spec 2026-11-23 §2): el estado de la plantilla de recordatorio de la conexión guardada. Devuelve
/// el estado de Meta tal cual (<c>APPROVED</c>, <c>PENDING</c>, <c>REJECTED</c>…) y su motivo, o <see cref="ReminderTemplateStates.Missing"/>
/// si la conexión no tiene la fila o el tenant no tiene el módulo de Mensajería. El adaptador vive en Bootstrapper.
/// </summary>
public interface ISchedulingReminderTemplateStatus
{
    Task<ReminderTemplateStateDto> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
