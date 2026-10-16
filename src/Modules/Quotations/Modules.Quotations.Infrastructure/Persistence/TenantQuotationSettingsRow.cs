namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>A tenant's minimum-purchase units (spec D7). A persistence row, not an aggregate:
/// it has no behaviour of its own; QuotationSettings.Create is where the rules live.</summary>
internal sealed class TenantQuotationSettingsRow
{
    public Guid TenantId { get; set; }

    public int MinimumUnits { get; set; }

    /// <summary>La tasa de IVA del envío (spec 2026-10-17): un <c>tax_rates.id</c> de Tenancy, sin FK
    /// porque cruza módulos. Nula hasta que el tenant la elige.</summary>
    public Guid? FreightTaxRateId { get; set; }
}
