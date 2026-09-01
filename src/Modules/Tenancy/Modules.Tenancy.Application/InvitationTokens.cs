using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Tenancy.Application;

/// <summary>
/// Genera y hashea los tokens de invitación. El token plano son 32 bytes aleatorios en
/// base64url —apto como segmento de URL, sin relleno— y viaja únicamente por el evento de
/// dominio hacia el email; en la base queda sólo el SHA-256 en hex minúsculo, así que
/// leer la tabla no alcanza para armar un link válido. Vive en Application porque decidir
/// qué se persiste y qué viaja es regla del flujo, no acceso a datos: la búsqueda por hash
/// sí es del repositorio (Infrastructure).
/// </summary>
public static class InvitationTokens
{
    public static string Generate() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string HashOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
