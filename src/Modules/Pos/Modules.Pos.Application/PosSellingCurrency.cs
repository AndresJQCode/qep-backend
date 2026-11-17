using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>La moneda y el IVA con que el cajero vende ahora (spec 2026-11-17 R4).</summary>
internal sealed record PosSellingContext(string Currency, bool ChargesVat)
{
    /// <summary>La tasa con que se cobra un producto de <paramref name="productTaxPercentage"/>.</summary>
    public int EffectiveTaxPercentage(int productTaxPercentage) => ChargesVat ? productTaxPercentage : 0;
}

/// <summary>
/// La moneda en la que el cajero vende ahora, para las lecturas que no reciben caja (búsqueda,
/// escaneo, vista previa): la de su caja abierta, congelada al abrir; sin caja, la vigente del
/// tenant, que es con la que abriría. Así la vista previa cotiza con la misma lista que el POST cobra.
/// La vista previa necesita además el IVA congelado de la caja (<see cref="ResolveContextAsync"/>); sin
/// caja es <c>true</c>, como hasta ahora.
/// </summary>
internal static class PosSellingCurrency
{
    public static async Task<string> ResolveAsync(
        ICashSessionRepository sessions,
        IMembershipDirectory membershipDirectory,
        ITenantDefaultCurrency tenantDefaultCurrency,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        (await ResolveContextAsync(
            sessions, membershipDirectory, tenantDefaultCurrency, executionContext, tenantId, cancellationToken)).Currency;

    public static async Task<PosSellingContext> ResolveContextAsync(
        ICashSessionRepository sessions,
        IMembershipDirectory membershipDirectory,
        ITenantDefaultCurrency tenantDefaultCurrency,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Sin membresía no hay caja que buscar; no es un 403 nuevo: el permiso ya se verificó y una
        // lectura de catálogo no depende de ser cajero.
        var membershipId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, tenantId, cancellationToken);
        if (membershipId is { } member
            && await sessions.FindOpenByCashierAsync(tenantId, new MemberId(member), cancellationToken) is { } session)
        {
            return new PosSellingContext(session.Currency, session.CompanyChargesVat);
        }

        return new PosSellingContext(await tenantDefaultCurrency.GetAsync(tenantId, cancellationToken), ChargesVat: true);
    }
}
