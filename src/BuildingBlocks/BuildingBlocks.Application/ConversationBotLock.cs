namespace BuildingBlocks.Application;

/// <summary>
/// Spec 2026-10-29 §6.2 paso 1: el candado de conversación de los bots, <c>pg_(try_)advisory_xact_lock(Namespace,
/// hashtext(conversation_id::text))</c>. Lo toman el motor de chatbots (Automations) y el procesador y el worker de los agentes (Ai) con el
/// <b>mismo</b> espacio y el mismo hash, para que los dos nunca trabajen la misma conversación a la vez; y el borrado de clientes, antes de
/// bloquear las conversaciones. Vive acá porque los dos módulos no se ven entre sí.
/// </summary>
public static class ConversationBotLock
{
    /// <summary>Primera clave del lock de dos claves: la fecha del plan de Automations. Cualquier constante sirve mientras ningún otro lock de
    /// dos claves la use. No se cambia: es el mismo valor que ya toman los procesos en curso.</summary>
    public const int Namespace = 20261022;
}
