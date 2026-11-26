using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Support.Application;

/// <summary>
/// Fase 4b (H18, owner 2026-11-16, Q-11): el único mensaje que pide el comentario después de que el contacto toca un botón de la encuesta.
/// Sale una vez, por el envío de respuestas de Soporte (no es una respuesta del asesor ni toma la conversación), dentro de la ventana que
/// abrió el toque; sin reintento ni recordatorio.
/// </summary>
public static class SupportSurveyPrompt
{
    /// <summary>El texto, fijo (owner 2026-11-16).</summary>
    public const string Text = "Gracias. Si quieres, cuéntanos en un mensaje por qué elegiste esa opción.";

    /// <summary>El <c>ClientId</c> del mensaje: el mismo para la misma encuesta, así un tick que se repite (el proceso murió entre el envío
    /// y la anotación) recibe de Mensajería el mensaje que ya salió en vez de mandar otro. Nunca es el de la plantilla de la encuesta (ese
    /// es el id de la encuesta, un GUID v7) ni el de una respuesta. Se deriva como <c>SupportOpeningSender.ClientIdFor</c>: versión 8.</summary>
    public static Guid ClientIdFor(Guid surveyId)
    {
        var seed = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"support.survey.prompt:{surveyId:N}"));
        var bytes = SHA256.HashData(seed).AsSpan(0, 16).ToArray();
        // Versión 8 (definida por la aplicación) y la variante RFC 9562, en el orden de bytes de System.Guid.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
