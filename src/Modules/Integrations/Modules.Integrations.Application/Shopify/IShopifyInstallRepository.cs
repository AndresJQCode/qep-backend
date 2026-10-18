using Modules.Integrations.Domain;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §9: <c>integrations.shopify_installs</c>. Es la única tabla de Integrations sin <c>tenantId</c> en sus
/// métodos, a propósito: una instalación no es de ningún tenant hasta el enlace (el inicio B ni siquiera tiene sesión),
/// y la protege el <c>state</c> aleatorio de un solo uso, no el tenant. Se guarda con <see cref="IIntegrationsUnitOfWork"/>.
/// </summary>
public interface IShopifyInstallRepository
{
    void Add(ShopifyInstall install);
}
