namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-09 §10.3: <c>failureReason</c> en español, calculado al leer desde <c>failure_code</c>
/// (cambiar un texto no pide migración). Sólo códigos verificados en la documentación de la Cloud API;
/// cualquier otro, el texto genérico. <c>failure_title</c> nunca sale por HTTP.
/// </summary>
public static class MessageFailureReasons
{
    /// <summary>§8.3: envío sin confirmar (timeout, 5xx o red); un acuse real de Meta lo corrige.</summary>
    public const int Unconfirmed = -1;

    public const string Generic = "WhatsApp no pudo entregar el mensaje.";

    private static readonly Dictionary<int, string> Texts = new()
    {
        [131047] = "Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.",
        [131026] = "WhatsApp no pudo entregar el mensaje: puede que el número no use WhatsApp o tenga una versión desactualizada.",
        [131051] = "WhatsApp no admite este tipo de mensaje.",
        [131049] = "WhatsApp no entregó el mensaje para cuidar la experiencia de la persona. Espera al menos 24 horas antes de volver a intentarlo.",
        [131050] = "La persona dejó de recibir mensajes de marketing de tu organización.",
        [131021] = "No puedes enviarle un mensaje al mismo número que lo envía.",
        [131031] = "La cuenta de WhatsApp Business está restringida o no pasó una verificación.",
        [131042] = "Hay un problema con el método de pago de la cuenta de WhatsApp Business.",
        [131056] = "Enviaste demasiados mensajes a esta persona en poco tiempo. Espera un momento y vuelve a intentarlo.",
        [133010] = "El número de tu organización no está registrado en WhatsApp Business.",
        [368] = "La cuenta de WhatsApp Business está restringida o deshabilitada por incumplir políticas.",
        [131005] = "QEP perdió los permisos sobre tu cuenta de WhatsApp. Vuelve a conectar el número.",
        [131016] = "WhatsApp no está disponible en este momento. Intenta de nuevo en unos minutos.",
        [131000] = "WhatsApp no pudo enviar el mensaje por un error desconocido. Intenta de nuevo.",

        // Spec 2026-10-11 §5.3: plantillas. Ninguno se reintenta tal cual: o cambia la plantilla o cambia el valor.
        [132000] = "Las variables de la plantilla no coinciden con las que WhatsApp espera. Actualiza las plantillas y vuelve a intentarlo.",
        [132001] = "La plantilla no existe en WhatsApp o no está aprobada en ese idioma. Actualiza las plantillas y elige otra.",
        [132015] = "WhatsApp pausó esta plantilla por su calidad. Elige otra o revisa su estado en WhatsApp Manager.",
        [132016] = "WhatsApp deshabilitó esta plantilla. Elige otra o crea una nueva en WhatsApp Manager.",
        [132018] = "WhatsApp rechazó el valor de una variable: no puede tener saltos de línea, tabulaciones ni más de 4 espacios seguidos.",
        [Unconfirmed] = "No pudimos confirmar el envío con WhatsApp. Intenta de nuevo.",

        // Spec 2026-10-14 §6.2: los de la Messaging API de Instagram. 10 es «permiso denegado» de Graph y Meta lo usa, entre otros,
        // cuando pasaron las 24 horas; 551 es la persona que no está disponible (bloqueó la cuenta o borró la suya).
        [10] = "Instagram no permitió el envío: puede que hayan pasado más de 24 horas desde el último mensaje de la persona.",
        [551] = "Instagram no pudo entregar el mensaje: la persona no está disponible.",
    };

    public static string? For(int? failureCode) =>
        failureCode is null ? null : Texts.GetValueOrDefault(failureCode.Value, Generic);
}
