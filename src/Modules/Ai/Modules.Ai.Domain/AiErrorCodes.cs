namespace Modules.Ai.Domain;

/// <summary>Spec 2026-10-29 §4.7 y plan 2026-11-12: los códigos del módulo. Son contrato con el frontend, que tiene el diccionario de
/// mensajes; no se renombran.</summary>
public static class AiErrorCodes
{
    /// <summary>404: el agente no existe en el tenant.</summary>
    public const string AgentNotFound = "ai.agent.not_found";

    /// <summary>422: nombre vacío o de más de <see cref="Agent.NameMaxLength"/> caracteres.</summary>
    public const string AgentNameInvalid = "ai.agent.name_invalid";

    /// <summary>409 (D-A11): ya hay un agente con ese nombre en el tenant, sin importar mayúsculas.</summary>
    public const string AgentNameTaken = "ai.agent.name_taken";

    /// <summary>409 (§4.3): otro bot (chatbot o agente) ya atiende una de las conexiones; lleva <c>occupant</c>.</summary>
    public const string AgentConnectionTaken = "ai.agent.connection_taken";

    /// <summary>422 (D-A7): publicar con errores; los problemas viajan en <c>errors</c>, por ruta del <c>config</c>.</summary>
    public const string AgentInvalid = "ai.agent.invalid";

    /// <summary>422: pausar o reanudar un agente que nunca se publicó.</summary>
    public const string AgentNotPublished = "ai.agent.not_published";

    public const string AgentAlreadyPaused = "ai.agent.already_paused";

    public const string AgentAlreadyActive = "ai.agent.already_active";

    /// <summary>422: una conexión de un canal sin salida de bot (hoy, todo lo que no es WhatsApp ni Instagram).</summary>
    public const string AgentChannelNotSupported = "ai.agent.channel_not_supported";

    /// <summary>409 (§4.7): borrar un agente que todavía atiende conversaciones (sesiones vivas). Se pausa y se espera a que terminen.</summary>
    public const string AgentHasLiveSessions = "ai.agent.has_live_sessions";

    /// <summary>409 (spec 2026-11-17 D-B18): borrar un agente que usa un chatbot publicado; lleva <c>chatbots: [{ id, name }]</c>.</summary>
    public const string AgentInUseByChatbots = "ai.agent.in_use_by_chatbots";

    /// <summary>404 (spec 2026-11-23 §4): el <c>templateId</c> no es de ninguna plantilla de agente del sistema; una de chatbot tampoco.</summary>
    public const string TemplateNotFound = "ai.template.not_found";

    /// <summary>422 (spec 2026-11-23 §4): alguna clave de <c>requires</c> de la plantilla no está efectiva para el tenant (por ejemplo, una
    /// pestaña vieja que todavía mostraba la plantilla como disponible).</summary>
    public const string TemplateModuleNotEnabled = "ai.template.module_not_enabled";

    /// <summary>404: una conexión que no es del tenant (no se dice de quién es).</summary>
    public const string ConnectionNotFound = "ai.connection.not_found";

    /// <summary>422 (plan 2026-11-12 C-18): publicar sin una conexión <c>Active</c> del proveedor del agente; lleva <c>provider</c>.</summary>
    public const string ProviderNotConfigured = "ai.provider.not_configured";

    /// <summary>404 (§4.7): la prueba no existe en el tenant, o ya venció (<c>MapException</c> no tiene 410).</summary>
    public const string TestNotFound = "ai.test.not_found";

    /// <summary>409 (§4.7): la prueba ya tiene un mensaje en curso.</summary>
    public const string TestBusy = "ai.test.busy";

    /// <summary>422 (spec 2026-11-17 copiloto §5.2): el tenant no configuró el copiloto o lo apagó.</summary>
    public const string CopilotDisabled = "ai.copilot.disabled";

    /// <summary>404: el hilo no existe en el tenant <b>o es de otro miembro</b>. Nunca 403: es un recurso privado y un 403 confirmaría que
    /// existe (D-C8).</summary>
    public const string CopilotThreadNotFound = "ai.copilot.thread_not_found";

    /// <summary>409 (D-C11): el hilo ya tiene una respuesta en curso.</summary>
    public const string CopilotBusy = "ai.copilot.busy";

    /// <summary>429 (D-C11): el miembro pasó su tope por hora o por día; lleva <c>retryAfterSeconds</c> y el header <c>Retry-After</c>.</summary>
    public const string CopilotRateLimited = "ai.copilot.rate_limited";

    /// <summary>429 (D-C11): el tenant gastó su tope mensual de tokens; lleva <c>retryAfterSeconds</c> hasta el mes siguiente del tenant.</summary>
    public const string CopilotBudgetExhausted = "ai.copilot.budget_exhausted";

    /// <summary>422 al guardar la configuración: el modelo no está en la lista curada del proveedor, está retirado, o falta para prender.</summary>
    public const string CopilotModelNotAvailable = "ai.copilot.model_not_available";

    /// <summary>409 (plan fase 2, C1.5; código nuevo, no está en el spec): reintentar una respuesta que no es la última fallida del hilo. Pasa
    /// sólo en una carrera (otra pestaña ya la reintentó); la pantalla relee el hilo.</summary>
    public const string CopilotNotRetryable = "ai.copilot.not_retryable";
}
