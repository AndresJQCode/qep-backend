using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// <see cref="ISupportPinHasher"/> sobre el hasher de ASP.NET Core Identity con las opciones por defecto (PBKDF2, formato V3), como
/// <c>AspNetCoreBuyerPasswordHasher</c> (plan F8). No pide un secreto nuevo en el pipeline. Sin estado por request: singleton.
/// </summary>
internal sealed class Pbkdf2SupportPinHasher : ISupportPinHasher
{
    private static readonly PasswordHasher<object> Hasher = new();

    // Se calcula una vez por proceso: verificar contra él cuesta lo mismo que contra un hash real, así un radicado inexistente tarda
    // lo mismo que uno con el PIN errado.
    private static readonly Lazy<string> DecoyHash =
        new(() => Hasher.HashPassword(new object(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public string Hash(string pin) => Hasher.HashPassword(new object(), pin);

    public bool Verify(string? hash, string pin)
    {
        if (hash is null)
        {
            Hasher.VerifyHashedPassword(new object(), DecoyHash.Value, pin);
            return false;
        }

        return Hasher.VerifyHashedPassword(new object(), hash, pin) is not PasswordVerificationResult.Failed;
    }
}
