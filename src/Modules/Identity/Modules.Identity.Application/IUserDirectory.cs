namespace Modules.Identity.Application;

/// <summary>
/// Read-only lookup of basic user attributes for other modules (e.g. Notifications
/// resolving an invited user's email). Exposes only non-authoritative basic claims,
/// per the Identity/Authorization boundary (ADR 0015).
/// </summary>
public interface IUserDirectory
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);
}
