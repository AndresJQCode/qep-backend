using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPasswordHasher"/> sobre el hasher de ASP.NET Core Identity con las opciones por
/// defecto (formato V3). No guarda estado por request: se registra como singleton.
/// </summary>
internal sealed class AspNetCorePasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<User> Hasher = new();

    // Se calcula una vez por proceso: verificar contra él cuesta lo mismo que un hash real.
    private static readonly Lazy<string> DummyHash =
        new(() => Hasher.HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public string Hash(string password) => Hasher.HashPassword(null!, password);

    public PasswordVerificationOutcome Verify(string? hashedPassword, string providedPassword)
    {
        if (hashedPassword is null)
        {
            Hasher.VerifyHashedPassword(null!, DummyHash.Value, providedPassword);
            return PasswordVerificationOutcome.Failed;
        }

        return Hasher.VerifyHashedPassword(null!, hashedPassword, providedPassword) switch
        {
            PasswordVerificationResult.Success => PasswordVerificationOutcome.Success,
            PasswordVerificationResult.SuccessRehashNeeded =>
                PasswordVerificationOutcome.SuccessRehashNeeded,
            _ => PasswordVerificationOutcome.Failed,
        };
    }
}
