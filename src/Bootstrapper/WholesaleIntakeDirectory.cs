using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Lo que <see cref="PurchasesWholesaleOrderIntake"/> lee del lado del mayorista para crear el pedido: los términos y precios de
/// la relación (Wholesale) y el padrón del mayorista para el asesor (Tenancy, D-14). Una costura propia para que el adaptador se
/// pruebe sin base: <see cref="WholesaleOffers"/> es una clase concreta.
/// </summary>
internal interface IWholesaleIntakeDirectory
{
    /// <summary>Revalida la relación y la visibilidad (<see cref="WholesaleOffers.PriceVisibleAsync"/>).</summary>
    Task<(WholesaleTerms Terms, IReadOnlyDictionary<Guid, WholesaleOffer> Offers)> PriceVisibleAsync(
        Guid wholesalerTenantId, Guid buyerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Sin revalidar nada (<see cref="WholesaleOffers.OffersForOrderAsync"/>): para el pedido que ya existe (D-8).</summary>
    Task<WholesaleOrderOffers> OffersForOrderAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);

    Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>La costura sobre Wholesale y Tenancy; las consultas del padrón calcan <see cref="StoreMemberDirectory"/>.</summary>
internal sealed class WholesaleIntakeDirectory(WholesaleOffers offers, TenancyDbContext tenancy) : IWholesaleIntakeDirectory
{
    public Task<(WholesaleTerms Terms, IReadOnlyDictionary<Guid, WholesaleOffer> Offers)> PriceVisibleAsync(
        Guid wholesalerTenantId, Guid buyerTenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        offers.PriceVisibleAsync(wholesalerTenantId, buyerTenantId, variantIds, cancellationToken);

    public Task<WholesaleOrderOffers> OffersForOrderAsync(
        Guid wholesalerTenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken) =>
        offers.OffersForOrderAsync(wholesalerTenantId, buyerTenantId, currency, variantIds, cancellationToken);

    public Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var id = new MembershipId(membershipId);
        return tenancy.Memberships.AsNoTracking()
            .AnyAsync(
                membership => membership.Id == id
                    && membership.TenantId == tenant
                    && membership.State == MembershipState.Active,
                cancellationToken);
    }

    public async Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var owner = await tenancy.Tenants.AsNoTracking()
            .Where(row => row.Id == tenant)
            .Select(row => (MembershipId?)row.OwnerMembershipId)
            .SingleOrDefaultAsync(cancellationToken);
        return owner?.Value
            ?? throw new InvalidOperationException($"The tenant {tenantId} does not exist, so it has no owner membership.");
    }
}
