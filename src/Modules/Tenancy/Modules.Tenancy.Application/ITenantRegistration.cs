using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="Country">
/// Spec 2026-11-22: el país ISO-3166-1 alpha-2 con que nace el tenant. <c>null</c> deja el default <c>CO</c> (signup). Sólo lo
/// fija el alta invitada, con el país del distribuidor guardado: ningún cuerpo de request lo trae.
/// </param>
/// <param name="Language">Spec 2026-11-22 (i18n): <c>null</c> es el por defecto (es-419).</param>
public sealed record TenantRegistrationData(
    string DisplayName,
    string Slug,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    string? Country = null,
    string? Language = null);

/// <summary>
/// Crea un tenant nuevo y su membresía de owner en una sola unidad de trabajo (ADR 0017).
/// Sólo es alcanzable cuando el signup público de tenants está habilitado; el llamador es
/// responsable de aprovisionar primero el usuario owner y de hacer cumplir el feature flag.
/// </summary>
public interface ITenantRegistration
{
    /// <returns>El id del tenant creado.</returns>
    Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId,
        TenantRegistrationData data,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Valida el formato de los datos y que el slug esté libre, sin escribir nada. Corre antes de
    /// cualquier rama por usuario, para que un 422 por slug tomado no delate si un email existe.
    /// </summary>
    /// <exception cref="TenantDomainException">tenancy.slug.invalid, tenancy.settings.*.invalid o tenancy.slug.taken.</exception>
    Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-17 (mayorista), D-3 y D-17: el alta del tenant de un distribuidor al aceptar la invitación, con el id que
    /// se reservó al invitar y bajo el candado de ese tenant. Idempotente: si el tenant ya existe y su owner es una membresía
    /// de <paramref name="ownerUserId"/> (un intento anterior cortado o un doble clic), no escribe nada y devuelve
    /// <c>false</c>. Los módulos son los que pide quien llama, con origen <c>invitation</c>, no los del signup. <b>No</b> mira
    /// <c>Registration:PublicTenantSignupEnabled</c>: la invitación es la autorización, y lo decide quien llama.
    /// </summary>
    /// <returns><c>true</c> si creó el tenant; <c>false</c> si ya existía con ese owner.</returns>
    /// <exception cref="InvalidOperationException">El tenant reservado existe con otro owner.</exception>
    Task<bool> RegisterInvitedTenantAsync(
        TenantId tenantId,
        Guid ownerUserId,
        TenantRegistrationData data,
        IReadOnlyList<TenantModuleKey> modules,
        string correlationId,
        CancellationToken cancellationToken);
}
