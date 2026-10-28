using System.Security.Cryptography;
using System.Text;

namespace Modules.Identity.Application;

/// <summary>
/// Misma receta que las sesiones: 256 bits aleatorios en base64url para el valor crudo (seguro en
/// un link) y SHA-256 en hex minúscula para lo que se guarda. SHA-256 alcanza porque la entrada es
/// de alta entropía, no un secreto de baja entropía que se pueda romper por fuerza bruta.
/// </summary>
public static class EmailTokenCodec
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}