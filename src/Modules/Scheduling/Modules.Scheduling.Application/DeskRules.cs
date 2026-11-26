using System.Linq.Expressions;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>Las dos reglas del escritorio que no son una consulta: qué es «sin marcar» y cuánto hace que se pidió una cita.</summary>
public static class DeskRules
{
    /// <summary>
    /// «Sin marcar» (plan 2026-11-21, «Contrato HTTP»): una cita <c>Confirmed</c> cuyo fin ya pasó, o sea que nadie dijo si el cliente llegó.
    /// Es una expresión y no un método para que el repositorio la traduzca a SQL y el contador y la lista salgan de la misma regla.
    /// </summary>
    public static Expression<Func<Appointment, bool>> Unmarked(DateTimeOffset now) =>
        appointment => appointment.Status == AppointmentStatus.Confirmed && appointment.EndsAt < now;

    /// <summary>Minutos enteros desde <paramref name="requestedAt"/>. Una cita con fecha de creación en el futuro (reloj atrasado) es 0, no negativo.</summary>
    public static int RequestedAgoMinutes(DateTimeOffset requestedAt, DateTimeOffset now) =>
        (int)Math.Max(0, (now - requestedAt).TotalMinutes);
}
