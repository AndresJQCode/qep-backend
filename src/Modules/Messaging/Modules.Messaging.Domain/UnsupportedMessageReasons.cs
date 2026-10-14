namespace Modules.Messaging.Domain;

/// <summary>
/// El <c>unsupportedReason</c> de un entrante <c>Unsupported</c>, en español y calculado al leer desde el código de
/// Meta que la ingesta guarda en <c>details.errors</c> (cambiar un texto no pide migración), como
/// <see cref="MessageFailureReasons"/>. Sólo códigos de la referencia de mensajes no soportados de Meta; cualquier otro
/// no tiene texto y la burbuja dibuja la etiqueta del kind.
/// </summary>
public static class UnsupportedMessageReasons
{
    private static readonly Dictionary<int, string> Texts = new()
    {
        // «Message unavailable»: la persona sí escribió, pero Meta no entregó el contenido. Pasó el 2026-10-14 con el
        // primer mensaje después de volver a registrar el número; los siguientes llegaron bien.
        [131060] = "WhatsApp no entregó el contenido de este mensaje. Pídele a la persona que lo envíe de nuevo.",
        // «Unsupported message type»: la Cloud API no entrega ese tipo.
        [131051] = "WhatsApp no admite este tipo de mensaje. Pídele a la persona que lo envíe como texto, imagen o documento.",
    };

    public static string? For(int? metaErrorCode) =>
        metaErrorCode is { } code ? Texts.GetValueOrDefault(code) : null;
}
