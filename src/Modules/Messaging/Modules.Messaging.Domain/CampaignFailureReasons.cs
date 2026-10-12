using System.Globalization;

namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-11 §6.1 y §6.3: los códigos con los que un destinatario queda <c>Skipped</c> o <c>Failed</c> (además del código numérico
/// de Meta como texto: <c>"131049"</c>), y la clasificación de los errores de Graph del worker. Una sola copia: el worker decide con
/// <see cref="IsRetriable"/> y <see cref="StopsTheCampaign"/>; las pruebas ejercen las mismas tablas.
/// </summary>
public static class CampaignFailureCodes
{
    public const string OptedOut = "opted_out";
    public const string Inactive = "inactive";
    public const string Unreachable = "unreachable";
    public const string CampaignCancelled = "campaign_cancelled";
    public const string CampaignFailed = "campaign_failed";

    /// <summary>El proceso cayó con la llamada a Meta en vuelo: no se sabe si salió, y no se reenvía (decisión 12).</summary>
    public const string UnknownOutcome = "unknown_outcome";

    /// <summary>Se agotaron los reintentos de un timeout, un 5xx o una respuesta sin id: Meta no confirmó nunca.</summary>
    public const string Unconfirmed = "unconfirmed";

    /// <summary>Se agotaron los reintentos de una conexión que nunca llegó a establecerse: el mensaje no salió.</summary>
    public const string NotSent = "not_sent";

    /// <summary>La conexión dejó de estar activa (pausada, borrada o con atención pendiente) a mitad del envío.</summary>
    public const string ConnectionUnavailable = "connection_unavailable";

    /// <summary>Meta (§2): throughput y tasa por par emisor–destinatario. Los 5xx y los timeouts llegan como «sin confirmar», sin código.</summary>
    public static bool IsRetriable(int code) => code is MetaErrorCodes.ThroughputLimit or MetaErrorCodes.PairRateLimit;

    /// <summary>Decisión 15: plantilla inexistente (132001), pausada (132015) o deshabilitada (132016), y credenciales o número
    /// rechazados (190, 133010). Seguir enviando sólo junta fallas.</summary>
    public static bool StopsTheCampaign(int code) =>
        code is MetaErrorCodes.TemplateDoesNotExist or MetaErrorCodes.TemplatePaused or MetaErrorCodes.TemplateDisabled
        || MetaErrorCodes.IsRejectedCredentials(code);

    /// <summary>Los parámetros guardados de la campaña no se pueden leer: ningún destinatario se podría armar, así que la campaña se detiene.</summary>
    public const string InvalidParameters = "invalid_parameters";
}

/// <summary>
/// Texto en español (tuteo colombiano) del código de un destinatario o de una campaña, calculado al leer como
/// <see cref="MessageFailureReasons"/>: cambiar un texto no pide migración. Los numéricos son códigos de Meta y se reusan de allá.
/// </summary>
public static class CampaignFailureReasons
{
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        [CampaignFailureCodes.OptedOut] = "No quiere recibir promociones.",
        [CampaignFailureCodes.Inactive] = "El cliente está inactivo.",
        [CampaignFailureCodes.Unreachable] = "El cliente no tiene WhatsApp.",
        [CampaignFailureCodes.CampaignCancelled] = "La campaña se canceló.",
        [CampaignFailureCodes.CampaignFailed] = "La campaña se detuvo.",
        [CampaignFailureCodes.UnknownOutcome] = "No se pudo confirmar el envío; no se reintentó para evitar duplicados.",
        [CampaignFailureCodes.ConnectionUnavailable] = "La conexión de WhatsApp está pausada, necesita atención o ya no existe. Revísala en Integraciones.",
        [CampaignFailureCodes.NotSent] = "No se pudo conectar con WhatsApp; el mensaje no salió.",
        [CampaignFailureCodes.InvalidParameters] = "Las variables guardadas de la campaña no se pueden leer. Crea la campaña de nuevo.",
        ["190"] = "Meta rechazó las credenciales de la conexión de WhatsApp. Vuelve a conectar el número.",
        ["133010"] = "Meta rechazó las credenciales del número de la conexión: no está registrado en WhatsApp Business. Vuelve a conectarlo.",
    };

    public static string? For(string? code)
    {
        if (code is null)
        {
            return null;
        }

        if (Texts.TryGetValue(code, out var text))
        {
            return text;
        }

        if (code == CampaignFailureCodes.Unconfirmed)
        {
            return MessageFailureReasons.For(MessageFailureReasons.Unconfirmed);
        }

        return int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var graphCode)
            ? MessageFailureReasons.For(graphCode)
            : MessageFailureReasons.Generic;
    }
}
