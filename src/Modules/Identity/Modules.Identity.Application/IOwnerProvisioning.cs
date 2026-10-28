namespace Modules.Identity.Application;

/// <summary>
/// Aprovisiona el usuario owner de un tenant auto-registrado (ADR 0017). A diferencia de
/// <see cref="IProviderLinking"/>, esto NO está condicionado a invitación: dar de alta un
/// tenant nuevo necesariamente crea su primer usuario sin invitación previa. Es la única
/// excepción documentada al aprovisionamiento sólo-por-invitación y sólo es alcanzable
/// cuando el signup público de tenants está habilitado. Igual se exige un email verificado.
/// </summary>
public interface IOwnerProvisioning
{
    /// <summary>
    /// Un email vacío o sin verificar devuelve <c>Denied("email_not_verified")</c> sin crear ni
    /// tocar nada. El vínculo pasa por <see cref="IVerifiedProviderLinker"/>.
    /// </summary>
    Task<ProviderLinkOutcome> ProvisionOwnerAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken);
}
