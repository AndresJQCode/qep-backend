namespace Modules.Automations.Domain;

/// <summary>
/// Spec 2026-10-21 §4.3: los códigos del módulo, en un solo lugar. Los 422 salen por <c>AutomationsDomainException</c>; los 404,
/// por <c>ResourceNotFoundException</c> en Application; los 409 de unicidad, por la unidad de trabajo según el nombre del índice.
/// </summary>
public static class AutomationsErrorCodes
{
    public const string ChatbotNotFound = "automations.chatbot.not_found";

    /// <summary><c>IX_chatbots_tenant_name</c>: el nombre se compara sin mayúsculas.</summary>
    public const string ChatbotNameTaken = "automations.chatbot.name_taken";

    /// <summary>D-C12: el nombre llega vacío o con más de 80 caracteres (no está en §4.3; lo agrega el agregado).</summary>
    public const string ChatbotNameInvalid = "automations.chatbot.name_invalid";

    /// <summary>D-C6: más de 10 palabras clave distintas (ya normalizadas).</summary>
    public const string ChatbotKeywordsTooMany = "automations.chatbot.keywords_too_many";

    /// <summary>D-C6: una palabra clave (o la de reinicio) pasa de 40 caracteres.</summary>
    public const string ChatbotKeywordTooLong = "automations.chatbot.keyword_too_long";

    /// <summary>Publicar con al menos un error del validador.</summary>
    public const string ChatbotInvalid = "automations.chatbot.invalid";

    /// <summary>D-C4 e <c>IX_chatbot_connections_active</c>: la conexión ya la atiende otro chatbot activo.</summary>
    public const string ChatbotConnectionTaken = "automations.chatbot.connection_taken";

    public const string ChatbotChannelNotSupported = "automations.chatbot.channel_not_supported";

    public const string ChatbotNotPublished = "automations.chatbot.not_published";

    public const string ChatbotAlreadyPaused = "automations.chatbot.already_paused";

    public const string ChatbotAlreadyActive = "automations.chatbot.already_active";

    /// <summary>Al borrar: hay corridas vivas (<c>Running</c> o <c>Waiting</c>).</summary>
    public const string ChatbotHasLiveRuns = "automations.chatbot.has_live_runs";

    public const string TemplateNotFound = "automations.template.not_found";

    public const string SimulationNotFound = "automations.simulation.not_found";

    public const string SimulationExpired = "automations.simulation.expired";

    public const string ExternalApiNotFound = "automations.external_api.not_found";
}
