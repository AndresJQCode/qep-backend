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
