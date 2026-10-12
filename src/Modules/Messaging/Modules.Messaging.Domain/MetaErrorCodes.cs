namespace Modules.Messaging.Domain;

/// <summary>
/// Los códigos de error de la Cloud API de Meta que Messaging decide por número. Una sola copia: el envío humano de plantillas
/// (<c>TemplateOutbound</c>), el worker de campañas y la clasificación de <see cref="CampaignFailureCodes"/> leen de acá, para que
/// un código nuevo o corregido no quede en una de las dos rutas y en la otra no.
/// </summary>
public static class MetaErrorCodes
{
    /// <summary>Throughput de la cuenta: reintentable.</summary>
    public const int ThroughputLimit = 130429;

    /// <summary>Tasa por par emisor–destinatario: reintentable.</summary>
    public const int PairRateLimit = 131056;

    /// <summary>La persona apagó el marketing de la organización (decisión 10): su ficha queda con opt-out de origen <c>meta</c>.</summary>
    public const int MarketingOptOut = 131050;

    /// <summary>El token de la conexión venció o fue revocado: la conexión pasa a <c>NeedsAttention</c> (<c>token_expired</c>).</summary>
    public const int TokenExpired = 190;

    /// <summary>El número de la conexión no está registrado en WhatsApp Business: <c>number_unregistered</c>.</summary>
    public const int NumberUnregistered = 133010;

    /// <summary>La plantilla no existe en ese idioma (decisión 15).</summary>
    public const int TemplateDoesNotExist = 132001;

    /// <summary>La plantilla está pausada.</summary>
    public const int TemplatePaused = 132015;

    /// <summary>La plantilla está deshabilitada.</summary>
    public const int TemplateDisabled = 132016;

    /// <summary>Credenciales (190) o número (133010) rechazados: la conexión necesita atención y seguir enviando sólo junta fallas.</summary>
    public static bool IsRejectedCredentials(int code) => code is TokenExpired or NumberUnregistered;

    /// <summary>El motivo con el que se reporta la conexión rechazada a Integrations.</summary>
    public static string RejectedReason(int code) => code == TokenExpired ? "token_expired" : "number_unregistered";
}
