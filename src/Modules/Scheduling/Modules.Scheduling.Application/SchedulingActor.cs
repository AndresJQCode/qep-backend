namespace Modules.Scheduling.Application;

/// <summary>
/// Quién escribe, separado de cómo se autorizó (spec 2026-11-23 §6.2). Los handlers HTTP escriben como <see cref="Human"/>; el agente de IA,
/// por <c>IChannelScheduling</c>, como <see cref="Agent"/>. El núcleo de reserva, cancelación y cambio de hora no mira el
/// <c>IExecutionContext</c>: sólo este registro.
/// </summary>
/// <param name="HistoryMemberId">El miembro que queda en la línea del historial; nulo cuando lo hizo el cliente por un canal o el sistema.</param>
/// <param name="ByChannel">Verdadero sólo para el agente (D-17): marca la línea del historial como «Cliente por WhatsApp». Ninguna escritura por HTTP la pone.</param>
/// <param name="AuditActorId">El actor de <c>audit.entries</c>: el sujeto de la sesión o el id del agente.</param>
/// <param name="IsAgent">Con verdadero la auditoría se escribe con el tipo de actor <c>AiAgent</c> y no <c>Human</c>.</param>
public sealed record SchedulingActor(Guid? HistoryMemberId, bool ByChannel, Guid AuditActorId, bool IsAgent)
{
    /// <summary>Quien opera con sesión: el sujeto es el actor de auditoría y la membresía (si la hay) firma el historial.</summary>
    public static SchedulingActor Human(Guid subjectId, Guid? memberId) => new(memberId, false, subjectId, false);

    /// <summary>El agente de IA que atiende al cliente por el canal: sin miembro en el historial, con <c>byChannel</c> y auditado a su nombre.</summary>
    public static SchedulingActor Agent(Guid agentId) => new(null, true, agentId, true);
}
