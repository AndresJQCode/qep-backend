using BuildingBlocks.Domain.Taxes;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Siigo.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 4: lo que el asistente ofrece vincular con un impuesto de Siigo. <b>IVA:</b> los porcentajes enteros de las
/// tasas que usan los productos activos del tenant (las variantes heredan la del producto), siempre con el 0: una línea al 0 % va sin
/// <c>taxes</c>. <b>Retenciones:</b> las activas nacionales (Retefuente, iguales para toda empresa) y, de las municipales, las que la empresa
/// tiene asignadas (<c>company.WithholdingTaxRateIds</c>). Un tenant tiene un
/// puñado de tasas (<c>ITaxRateRepository</c>): se lee el tenant entero y se filtra en memoria.
/// </summary>
internal sealed class SiigoTaxRateDirectory(CatalogDbContext catalog, ITaxRateRepository taxRates, ICompanyRepository companies)
    : ISiigoTaxRateDirectory
{
    public async Task<IReadOnlyList<int>> ListVatPercentagesInUseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var usedRateIds = await catalog.Products
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId && product.IsActive && product.TaxRateId != null)
            .Select(product => product.TaxRateId!.Value.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var used = usedRateIds.ToHashSet();

        var percentages = (await taxRates.ListAsync(tenantId, cancellationToken))
            .Where(rate => used.Contains(rate.Id.Value))
            // Sólo el IVA (el que suma) y entero: lo que una línea puede llevar como porcentaje (TaxRate.VatPercentage).
            .Where(rate => rate.Behavior == TaxBehavior.Adds && rate.Percentage == decimal.Truncate(rate.Percentage))
            .Select(rate => rate.VatPercentage);
        return [.. percentages.Append(0).Distinct().Order()];
    }

    public async Task<IReadOnlyList<SiigoWithholdingRef>> ListWithholdingRatesAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return [];
        }

        // Las nacionales (Retefuente) valen igual para toda empresa y nunca se asignan a una: se ofrecen siempre. Las municipales
        // (ReteICA) cambian por ciudad y sólo se ofrecen las que la empresa tiene asignadas.
        var assigned = company.WithholdingTaxRateIds.ToHashSet();
        return
        [
            .. (await taxRates.ListAsync(tenantId, cancellationToken))
                .Where(rate => rate.IsActive && rate.Withholds
                    && (assigned.Contains(rate.Id.Value) || !TaxCountries.IsMunicipal(rate.Country, rate.Type)))
                .Select(rate => new SiigoWithholdingRef(rate.Id.Value, rate.Name, rate.Type, rate.Percentage)),
        ];
    }
}
