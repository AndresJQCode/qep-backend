using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.1 y §7: el nombre de la empresa de la cuenta de cobro y el de la clasificación por defecto de la
/// tienda. Los dos repositorios filtran por tenant: una de otro tenant da <c>null</c>.</summary>
internal sealed class ShopifyStoreReferences(ICompanyRepository companies, IClientClassificationRepository classifications)
    : IShopifyStoreReferences
{
    public async Task<string?> CompanyNameAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        (await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken))?.Name;

    public async Task<string?> ClassificationNameAsync(Guid tenantId, Guid classificationId, CancellationToken cancellationToken) =>
        (await classifications.FindAsync(tenantId, new ClientClassificationId(classificationId), cancellationToken))?.Name;
}
