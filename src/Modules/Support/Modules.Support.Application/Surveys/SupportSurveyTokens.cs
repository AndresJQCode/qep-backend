using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Support.Application;

/// <summary>
/// Fase 4b (H19, owner 2026-11-16, Q-12): el token del enlace de calificación por correo. 32 bytes aleatorios en base64url (43
/// caracteres, sin relleno); sólo su SHA-256 se guarda (<c>ticket_surveys.token_hash</c>, <c>UX_ticket_surveys_token</c>). El token viaja
/// en el enlace del correo y, de vuelta, en el cuerpo de la API pública, nunca en una ruta ni en un log.
/// </summary>
public static class SupportSurveyTokens
{
    public const int TokenLength = 43;

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>El hash que se guarda y con que se busca la encuesta: SHA-256 del texto del token en UTF-8.</summary>
    public static byte[] HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Un token con la forma de los que se emiten: 43 caracteres base64url. Uno que no la tiene no se busca.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: TokenLength } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary><c>{Support:PublicForm:BaseUrl}/{slug}/calificar?token={token}</c> (<c>calificar</c> es un slug reservado).</summary>
    public static Uri RatingUrl(ISupportPublicOptions options, string slug, string token)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Uri($"{options.BaseUrl}/{slug}/calificar?token={token}");
    }
}
