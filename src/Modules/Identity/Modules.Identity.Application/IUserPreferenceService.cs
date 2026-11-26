namespace Modules.Identity.Application;

/// <summary>
/// Preferencia de apariencia efectiva de un usuario dentro de un tenant. Los valores viajan
/// como texto porque son identificadores de presentación, no dominio de Identity: el catálogo
/// de esquemas pertenece al módulo <c>account</c> del frontend.
///
/// <para><c>Language</c> es lo guardado: <c>es-419</c>, <c>es-ES</c> o <c>null</c> para seguir el del
/// tenant (spec 2026-11-22, i18n). Identity no ve Tenancy, así que el efectivo no sale de acá.</para>
/// </summary>
public sealed record UserPreferenceDto(string ColorScheme, string Mode, bool MessageSound, string? Language);

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
    /// Upsert idempotente, campo por campo: un valor <c>null</c> conserva el guardado (o el default
    /// para quien nunca eligió), para que el interruptor del sonido y el tema de otra pestaña no se
    /// pisen (spec 2026-10-14, contrato §2.2). Los cuatro <c>null</c> a la vez es
    /// <c>identity.preference.empty</c>. Lanza <c>IdentityDomainException</c> con
    /// <c>identity.preference.scheme.invalid</c>, <c>identity.preference.mode.invalid</c> o
    /// <c>identity.preference.language.invalid</c> si los valores no validan.
    ///
    /// <para><paramref name="language"/> sigue la misma regla, con un valor más: <c>""</c> (o sólo
    /// espacios) borra la elección y la persona vuelve a seguir el idioma del tenant. Hace falta
    /// porque <c>null</c> ya significa «no lo toques».</para>
    /// </summary>
    Task<UserPreferenceDto> SaveAsync(
        Guid userId,
        Guid tenantId,
        string? colorScheme,
        string? mode,
        bool? messageSound,
        string? language,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lo guardado para cada tenant de la lista, en una sola lectura: <c>null</c> si esa persona no
    /// eligió idioma ahí (o nunca guardó una preferencia). Lo usa <c>/auth/me</c>, que describe todos
    /// los tenants de la persona a la vez.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetLanguagesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken);
}
