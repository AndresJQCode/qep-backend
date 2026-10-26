namespace Modules.Automations.Domain;

// Se guardan y viajan por nombre (texto), como los de Pipeline: los CHECK de §4.2 listan los nombres y el frontend tiene el
// diccionario de etiquetas.

/// <summary>Spec 2026-10-21 §4.2 (<c>CK_chatbots_status</c>): borrador sin publicar, publicado y atendiendo, o publicado y en
/// pausa.</summary>
public enum ChatbotStatus
{
    Draft,
    Active,
    Paused,
}

/// <summary>Spec 2026-10-21 §4.2 (<c>CK_chatbots_trigger</c>): qué arranca una corrida.</summary>
public enum ChatbotTrigger
{
    NewConversation,
    Keyword,
    OutsideHours,
}

/// <summary>Spec 2026-10-25 D-2: qué hace el motor cuando se agotan los intentos de un bloque sin salida de agotamiento conectada.
/// <c>Restart</c> reinicia la corrida (el comportamiento de siempre); <c>Handoff</c> la pasa a un asesor. Se guarda por nombre.</summary>
public enum AttemptsExhaustedAction
{
    Restart,
    Handoff,
}
