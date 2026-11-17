namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>
/// El candado de conversación del runner (<see cref="AutomationsUnitOfWork.TryLockConversationAsync"/>):
/// <c>pg_advisory_xact_lock(Namespace, hashtext(conversation_id::text))</c>. Público para el borrado de clientes (spec 2026-11-10,
/// <c>Bootstrapper.CustomerDeletionReferences</c>), que lo toma antes de bloquear las conversaciones: el runner lo toma antes de su
/// corrida y, con los dos, escribe la conversación por otra conexión. Quien bloquee la conversación sin pasar antes por este candado
/// puede cerrar un ciclo que Postgres no detecta.
/// </summary>
public static class AutomationsConversationLock
{
    /// <summary>Spec 2026-10-29 §6.2: es <see cref="BuildingBlocks.Application.ConversationBotLock.Namespace"/>, compartido con los agentes de
    /// IA para que el motor y el agente nunca trabajen la misma conversación a la vez.</summary>
    public const int Namespace = BuildingBlocks.Application.ConversationBotLock.Namespace;
}
