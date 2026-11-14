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

    /// <summary>422: una conexión de un canal sin salida de bot (hoy, todo lo que no es WhatsApp).</summary>
    public const string AgentChannelNotSupported = "ai.agent.channel_not_supported";

    /// <summary>404: una conexión que no es del tenant (no se dice de quién es).</summary>
    public const string ConnectionNotFound = "ai.connection.not_found";

    /// <summary>422 (plan 2026-11-12 C-18): publicar sin una conexión <c>Active</c> del proveedor del agente; lleva <c>provider</c>.</summary>
    public const string ProviderNotConfigured = "ai.provider.not_configured";
}
