namespace Modules.Scheduling.Domain;

/// <summary>Sólo cuenta con cupo mayor que 1 y una franja más larga que el servicio (spec 2026-11-21, «Franjas»).</summary>
public enum SessionMode
{
    /// <summary>La primera reserva abre la sesión; las demás se suman al mismo servicio e inicio hasta llenar el cupo.</summary>
    FirstBookingOpens,

    /// <summary>El tenant publica las sesiones y sólo ésas se reservan.</summary>
    PublishedSessions,
}
