namespace Modules.Identity.Application;

public enum PasswordVerificationOutcome
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>
/// Puerto del hash de contraseñas. Application no conoce la implementación (ASP.NET Core
/// Identity vive sólo en Infrastructure).
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <param name="hashedPassword">
    /// Null verifica contra un hash ficticio fijo del proceso y devuelve
    /// <see cref="PasswordVerificationOutcome.Failed"/>: iguala el tiempo con el caso en que el
    /// usuario existe.
    /// </param>
    PasswordVerificationOutcome Verify(string? hashedPassword, string providedPassword);
}
