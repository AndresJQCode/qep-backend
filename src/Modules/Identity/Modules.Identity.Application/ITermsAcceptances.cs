using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// Spec 2026-11-19 (onboarding por link): la aceptación de los términos, por usuario, con la versión vigente. El texto, la URL y
/// la versión son decisión pendiente del owner; la versión sale de configuración (<see cref="TermsOptions"/>).
/// </summary>
public interface ITermsAcceptances
{
    /// <summary>
    /// Registra que <paramref name="userId"/> aceptó <paramref name="version"/>. Idempotente: aceptar dos veces la misma
    /// versión deja una sola fila. Se confirma solo, fuera de la unidad de trabajo del llamador, para que sea un paso
    /// idempotente más de la aceptación.
    /// </summary>
    /// <exception cref="IdentityDomainException">
    /// <c>identity.terms.unavailable</c> si no hay versión configurada (falla cerrado: nunca acepta una versión inventada);
    /// <c>identity.terms.version_mismatch</c> si <paramref name="version"/> no es la vigente.
    /// </exception>
    Task RecordAsync(Guid userId, string version, CancellationToken cancellationToken);

    /// <summary>Si el usuario aceptó la versión vigente. Una versión anterior no cuenta.</summary>
    /// <exception cref="IdentityDomainException"><c>identity.terms.unavailable</c> sin versión configurada.</exception>
    Task<bool> HasAcceptedCurrentAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Sección <c>Terms</c>. Sin validador de arranque a propósito: una clave requerida rompería todos los hosts de prueba. Sin
/// valor, el servicio falla cerrado en el momento de usarlo.
/// </summary>
public sealed class TermsOptions
{
    public const string SectionName = "Terms";

    public string? CurrentVersion { get; init; }
}

public interface ITermsAcceptanceRepository
{
    /// <summary>Inserta la aceptación si no existe ya la de ese usuario y versión. Una sola sentencia que se confirma sola;
    /// devuelve si insertó.</summary>
    Task<bool> AddIfMissingAsync(TermsAcceptance acceptance, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid userId, string version, CancellationToken cancellationToken);
}

public sealed class TermsAcceptanceService(
    ITermsAcceptanceRepository acceptances,
    TermsOptions options,
    IClock clock)
    : ITermsAcceptances
{
    public const string UnavailableCode = "identity.terms.unavailable";

    public const string VersionMismatchCode = "identity.terms.version_mismatch";

    public async Task RecordAsync(Guid userId, string version, CancellationToken cancellationToken)
    {
        var current = CurrentVersion();
        if (!string.Equals(version?.Trim(), current, StringComparison.Ordinal))
        {
            throw new IdentityDomainException(
                VersionMismatchCode, "The accepted terms version is not the current one.");
        }

        await acceptances.AddIfMissingAsync(
            TermsAcceptance.Accept(TermsAcceptanceId.New(), new UserId(userId), current, clock.UtcNow),
            cancellationToken);
    }

    public Task<bool> HasAcceptedCurrentAsync(Guid userId, CancellationToken cancellationToken) =>
        acceptances.ExistsAsync(userId, CurrentVersion(), cancellationToken);

    private string CurrentVersion()
    {
        var current = options.CurrentVersion?.Trim();
        return string.IsNullOrEmpty(current)
            ? throw new IdentityDomainException(UnavailableCode, "No terms version is configured.")
            : current;
    }
}
