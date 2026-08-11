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
    Task<Guid> ProvisionOwnerAsync(
        string provider,
        string subject,
        string email,
        CancellationToken cancellationToken);
}
