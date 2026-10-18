using Modules.Integrations.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §9: <c>integrations.shopify_installs</c>. Es la única tabla de Integrations sin <c>tenantId</c> en sus
/// métodos, a propósito: una instalación no es de ningún tenant hasta el enlace (el inicio B ni siquiera tiene sesión),
/// y la protege el <c>state</c> aleatorio de un solo uso, no el tenant. Cuando la fila trae <c>InitiatedTenantId</c>, el
/// handler que la use desde un tenant lo revalida (<c>install_tenant_mismatch</c>). <see cref="Add"/> se guarda con
/// <see cref="IIntegrationsUnitOfWork"/>.
/// </summary>
public interface IShopifyInstallRepository
{
    void Add(ShopifyInstall install);

    /// <summary>El enlace borra la fila en la misma transacción que crea la conexión (§4.3 paso 6): se guarda con
    /// <see cref="IIntegrationsUnitOfWork"/>. La instancia puede venir de <see cref="FindAsync"/>, sin tracking.</summary>
    void Remove(ShopifyInstall install);

    /// <summary>Sin tracking: el canje no la guarda con la unidad de trabajo sino con <see cref="TryCompleteExchangeAsync"/>.</summary>
    Task<ShopifyInstall?> FindByStateAsync(string state, CancellationToken cancellationToken);

    /// <summary>Sin tracking. La vigencia la decide quien llama, con su reloj.</summary>
    Task<ShopifyInstall?> FindAsync(Guid installId, CancellationToken cancellationToken);

    /// <summary>
    /// Guarda el reclamo ya aplicado en el agregado (<see cref="ShopifyInstall.Claim"/>) con un solo
    /// <c>UPDATE … SET state = @claim WHERE id = @id AND state = @state AND access_token_protected IS NULL</c>, antes de
    /// llamar a Shopify: es lo que consume el <c>state</c>. <c>false</c> = otro request lo reclamó entre la lectura y este
    /// UPDATE (doble clic del callback); el handler responde <c>install_not_found</c> sin gastar el code.
    /// </summary>
    Task<bool> TryClaimAsync(ShopifyInstall install, string state, CancellationToken cancellationToken);

    /// <summary>
    /// Guarda el canje ya aplicado en el agregado (<see cref="ShopifyInstall.MarkExchanged"/>) sobre la fila reclamada:
    /// <c>UPDATE … WHERE id = @id AND state = @claim AND access_token_protected IS NULL</c>. Después de un reclamo ganado
    /// no debería dar <c>false</c>; si lo da, el handler responde <c>install_not_found</c>.
    /// </summary>
    Task<bool> TryCompleteExchangeAsync(ShopifyInstall install, CancellationToken cancellationToken);
}
