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
        string? colorScheme,
        string? mode,
        bool? messageSound,
        string? language,
        CancellationToken cancellationToken)
    {
        if (colorScheme is null && mode is null && messageSound is null && language is null)
        {
            throw new IdentityDomainException(
                "identity.preference.empty",
                "At least one of colorScheme, mode, messageSound or language is required.");
        }

        var identifier = new UserId(userId);
        var existing = await dbContext.UserPreferences.SingleOrDefaultAsync(
            preference => preference.UserId == identifier && preference.TenantId == tenantId,
            cancellationToken);

        // Lo que no viene se conserva: lo guardado, o el default de quien nunca eligió. Así el PUT del
        // interruptor del sonido no pisa el tema que otra pestaña acaba de cambiar (spec 2026-10-14 §2.2).
        var current = existing ?? UserPreference.CreateDefault(identifier, tenantId, clock.UtcNow);
        var effectiveScheme = colorScheme ?? current.ColorScheme;
        var effectiveMode = mode ?? UserPreference.ToWireValue(current.Mode);
        var effectiveSound = messageSound ?? current.MessageSound;
        // null conserva; "" vuelve al idioma del tenant (lo guardado pasa a null).
        var effectiveLanguage = language is null
            ? current.Language
            : string.IsNullOrWhiteSpace(language) ? null : language;

        UserPreference preferenceToSave;
        if (existing is null)
        {
            // La validación corre dentro de Create/Change, antes de tocar el contexto: un
            // cuerpo inválido no puede dejar una fila a medias.
            preferenceToSave = UserPreference.Create(
                identifier,
                tenantId,
                effectiveScheme,
                effectiveMode,
                effectiveSound,
                clock.UtcNow,
                effectiveLanguage);
            dbContext.UserPreferences.Add(preferenceToSave);
        }
        else
        {
            existing.Change(effectiveScheme, effectiveMode, effectiveSound, clock.UtcNow, effectiveLanguage);
            preferenceToSave = existing;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(preferenceToSave);
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> GetLanguagesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        var identifier = new UserId(userId);
        var stored = await dbContext.UserPreferences
            .AsNoTracking()
            .Where(preference => preference.UserId == identifier && tenantIds.Contains(preference.TenantId))
            .Select(preference => new { preference.TenantId, preference.Language })
            .ToDictionaryAsync(row => row.TenantId, row => row.Language, cancellationToken);

        return tenantIds.Distinct().ToDictionary(
            tenantId => tenantId,
            tenantId => stored.GetValueOrDefault(tenantId));
    }

    private static UserPreferenceDto ToDto(UserPreference preference) =>
        new(
            preference.ColorScheme,
            UserPreference.ToWireValue(preference.Mode),
            preference.MessageSound,
            preference.Language);
}
