using BuildingBlocks.Application;
using BuildingBlocks.Domain.Taxes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Companies.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Adapta Catalog y Quotations al puerto <see cref="ICompanyDefaultTaxRates"/> de Companies (spec 2026-11-17 §5.3, R6).
/// Vive en el composition root porque Companies no ve a ninguno de los dos (<c>CompaniesLayerTests</c>).
///
/// **Todo va en una transacción de <see cref="CatalogDbContext"/>, bajo un advisory lock por tenant.** Sin el candado, dos
/// empresas creadas a la vez leerían las dos «no hay IVA» y sembrarían seis tasas. El <see cref="QuotationsDbContext"/> del
/// scope se enrola en la misma conexión y transacción (como <see cref="CatalogProductDeletionCleanup"/>), así que las tasas y
/// la del envío quedan juntas o no quedan. Después de confirmar, ese <see cref="QuotationsDbContext"/> apunta a una
/// transacción terminada: nada más en el scope debe usarlo, y el handler de la empresa ya no lo hace.
/// </summary>
internal sealed class CompanyDefaultTaxRates(
    CatalogDbContext catalog,
    QuotationsDbContext quotations,
    ITaxRateRepository rates,
    IQuotationSettingsStore settings,
    ICatalogAuditPublisher audit,
    IClock clock) : ICompanyDefaultTaxRates
{
    /// <summary>
    /// Primera clave del candado de dos claves; la segunda es <c>hashtext(tenantId)</c>. No la usa ningún otro candado de
    /// dos claves (ver la lista en <c>ShopifyTokenLock</c>).
    /// </summary>
    internal const int LockNamespace = 20261117;

    private const string ColombiaCountryCode = "CO";
    private const decimal FreightPercentage = 19m;

    // El orden importa sólo para la lectura de la auditoría; la del envío se busca por porcentaje.
    private static readonly (string Name, decimal Percentage)[] Defaults =
    [
        ("IVA 19 %", 19m),
        ("IVA 5 %", 5m),
        ("Exento", 0m),
    ];

    public async Task EnsureColombianVatAsync(Guid tenantId, Guid? actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await catalog.Database.BeginTransactionAsync(cancellationToken);
        await catalog.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({LockNamespace}, hashtext({tenantId.ToString()}))", cancellationToken);

        var existing = await rates.ListAsync(tenantId, cancellationToken);
        var created = new List<TaxRate>();
        if (!existing.Any(IsActiveColombianVat))
        {
            var now = clock.UtcNow;
            foreach (var (name, percentage) in Defaults)
            {
                // Una tasa con el mismo nombre y porcentaje, activa o no, chocaría con
                // IX_tax_rates_tenant_name_percentage_base: se salta en vez de reactivarla, que es decisión del tenant.
                if (existing.Any(rate => rate.Name == name && rate.Percentage == percentage && rate.MinimumBase is null))
                {
                    continue;
                }

                var rate = TaxRate.Create(
                    TaxRateId.New(), tenantId, name, percentage, TaxCountries.IvaType, ColombiaCountryCode, null, null, now);
                rates.Add(rate);
                audit.Publish(
                    tenantId, actorId ?? Guid.Empty, "catalog.tax_rate.created", rate.Id.ToString(), "success", now);
                created.Add(rate);
            }

            await catalog.SaveChangesAsync(cancellationToken);
        }

        quotations.Database.SetDbConnection(catalog.Database.GetDbConnection(), contextOwnsConnection: false);
        await quotations.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        if (await settings.GetFreightTaxRateIdAsync(tenantId, cancellationToken) is null)
        {
            // La recién creada; si no se creó ninguna, la activa de CO con 19 % que ya tenía el tenant. Sin ninguna de las
            // dos, queda vacía como antes y el envío cobrado con IVA sigue pidiendo que la elijan.
            var freight = created.FirstOrDefault(rate => rate.Percentage == FreightPercentage)
                ?? existing.FirstOrDefault(rate => IsActiveColombianVat(rate) && rate.Percentage == FreightPercentage);
            if (freight is not null)
            {
                await settings.SetFreightTaxRateIdAsync(tenantId, freight.Id.Value, cancellationToken);
                await quotations.SaveChangesAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsActiveColombianVat(TaxRate rate) =>
        rate.IsActive && rate.Type == TaxCountries.IvaType && rate.Country == ColombiaCountryCode;
}
