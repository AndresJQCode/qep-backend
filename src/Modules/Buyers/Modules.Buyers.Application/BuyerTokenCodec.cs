using System.Security.Cryptography;
using System.Text;

namespace Modules.Buyers.Application;

/// <summary>
/// Misma receta que <c>EmailTokenCodec</c> de Identity: 256 bits aleatorios en base64url para el valor crudo
/// (seguro en un enlace) y SHA-256 en hex minúscula para lo que se guarda. Sirve para tokens de email y de sesión.
/// </summary>
public static class BuyerTokenCodec
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
