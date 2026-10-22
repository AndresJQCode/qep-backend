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

    /// <summary><c>IX_conversation_runs_live</c> (D-M12): la conversación ya tiene una corrida viva.</summary>
    public const string RunAlreadyLive = "automations.run.already_live";

    /// <summary><c>GET /runs</c> con <c>from</c> posterior a <c>to</c>: no es del §4.3, lo agrega la tarea 8 del plan 2.</summary>
    public const string RunRangeInvalid = "automations.run.range_invalid";

    /// <summary><c>IX_chatbot_templates_tenant_name</c>: el nombre de la plantilla se compara sin mayúsculas.</summary>
    public const string TemplateNameTaken = "automations.template.name_taken";

    public const string TemplateNotFound = "automations.template.not_found";

    public const string SimulationNotFound = "automations.simulation.not_found";

    public const string SimulationExpired = "automations.simulation.expired";

    public const string ExternalApiNotFound = "automations.external_api.not_found";

    /// <summary>Crear o editar un chatbot con una conexión que no es del tenant (o que Integrations ya borró). No está en §4.3: lo
    /// agrega la tarea 14 del plan.</summary>
    public const string ConnectionNotFound = "automations.connection.not_found";
}
