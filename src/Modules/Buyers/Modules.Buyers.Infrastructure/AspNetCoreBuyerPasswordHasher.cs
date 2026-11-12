using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure;

/// <summary>
/// <see cref="IBuyerPasswordHasher"/> sobre el hasher de ASP.NET Core Identity con las opciones por defecto (formato V3).
/// No guarda estado por request: se registra como singleton.
/// </summary>
internal sealed class AspNetCoreBuyerPasswordHasher : IBuyerPasswordHasher
{
    private static readonly PasswordHasher<BuyerAccount> Hasher = new();

    // Se calcula una vez por proceso: verificar contra él cuesta lo mismo que un hash real.
    private static readonly Lazy<string> DummyHash =
        new(() => Hasher.HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public string Hash(string password) => Hasher.HashPassword(null!, password);

    public BuyerPasswordCheck Verify(string? hash, string password)
    {
        if (hash is null)
        {
            Hasher.VerifyHashedPassword(null!, DummyHash.Value, password);
            return BuyerPasswordCheck.Failed;
        }

        return Hasher.VerifyHashedPassword(null!, hash, password) switch
        {
            PasswordVerificationResult.Success => BuyerPasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => BuyerPasswordCheck.SuccessRehashNeeded,
            _ => BuyerPasswordCheck.Failed,
        };
    }
}
