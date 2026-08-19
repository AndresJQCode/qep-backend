namespace Modules.Identity.Application;

/// <summary>
/// Preferencia de apariencia efectiva de un usuario dentro de un tenant. Los valores viajan
/// como texto porque son identificadores de presentación, no dominio de Identity: el catálogo
/// de esquemas pertenece al módulo <c>account</c> del frontend.
/// </summary>
public sealed record UserPreferenceDto(string ColorScheme, string Mode);

/// <summary>
/// ACC-03. Lee y escribe la preferencia por el par <c>(userId, tenantId)</c>.
///
/// <para>Ninguno de los dos métodos verifica membresía, y no es un olvido: el
/// <c>tenantId</c> que reciben sale del claim que <c>ExternalClaimsTransformation</c> sólo
/// emite cuando ya comprobó que hay una membresía activa. Duplicar esa verificación acá
/// crearía una segunda autoridad sobre lo mismo, que es peor que no tenerla.</para>
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>
    /// Devuelve la preferencia guardada o el default si el usuario nunca eligió. **No
    /// persiste** en el segundo caso: una lectura no escribe.
    /// </summary>
    Task<UserPreferenceDto> GetAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upsert idempotente. Lanza <c>IdentityDomainException</c> con
    /// <c>identity.preference.scheme.invalid</c> o <c>identity.preference.mode.invalid</c>
    /// si los valores no validan.
    /// </summary>
    Task<UserPreferenceDto> SaveAsync(
        Guid userId,
        Guid tenantId,
        string colorScheme,
        string mode,
        CancellationToken cancellationToken);
}
