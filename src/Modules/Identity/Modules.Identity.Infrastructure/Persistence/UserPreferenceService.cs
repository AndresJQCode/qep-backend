using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ACC-03. Lectura y upsert de la preferencia de apariencia por <c>(userId, tenantId)</c>.
/// </summary>
internal sealed class UserPreferenceService(IdentityDbContext dbContext, IClock clock)
    : IUserPreferenceService
{
    public async Task<UserPreferenceDto> GetAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var identifier = new UserId(userId);
        var stored = await dbContext.UserPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                preference =>
                    preference.UserId == identifier && preference.TenantId == tenantId,
                cancellationToken);

        // Nunca eligió: se devuelve el default sin escribirlo. No tener preferencia es un
        // estado normal, no una fila faltante que haya que reparar, y una lectura que
        // escribe convierte cualquier GET en una mutación.
        var preferenceOrDefault = stored
            ?? UserPreference.CreateDefault(identifier, tenantId, clock.UtcNow);

        return ToDto(preferenceOrDefault);
    }

    public async Task<UserPreferenceDto> SaveAsync(
        Guid userId,
        Guid tenantId,
        string colorScheme,
        string mode,
        CancellationToken cancellationToken)
    {
        var identifier = new UserId(userId);
        var existing = await dbContext.UserPreferences.SingleOrDefaultAsync(
            preference => preference.UserId == identifier && preference.TenantId == tenantId,
            cancellationToken);

        UserPreference preferenceToSave;
        if (existing is null)
        {
            // La validación corre dentro de Create/Change, antes de tocar el contexto: un
            // cuerpo inválido no puede dejar una fila a medias.
            preferenceToSave = UserPreference.Create(
                identifier,
                tenantId,
                colorScheme,
                mode,
                clock.UtcNow);
            dbContext.UserPreferences.Add(preferenceToSave);
        }
        else
        {
            existing.Change(colorScheme, mode, clock.UtcNow);
            preferenceToSave = existing;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(preferenceToSave);
    }

    private static UserPreferenceDto ToDto(UserPreference preference) =>
        new(preference.ColorScheme, UserPreference.ToWireValue(preference.Mode));
}
