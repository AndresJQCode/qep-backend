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

    /// <summary>Las opciones de «¿Quién paga el envío?» que el tenant apagó (spec 2026-10-17 §4.4), por nombre del
    /// enum. Se guardan las apagadas y no las activas para que «sin tocar» sea la lista vacía: las filas de antes
    /// de la migración y las que crea <c>LockRowAsync</c> nacen con las tres activas sin escribir nada.</summary>
    public string[] DisabledFreightChargeModes { get; set; } = [];

    /// <summary>La opción de quién paga que el formulario elige por defecto (spec 2026-10-17 §4.4.1), por nombre
    /// del enum. Nula hasta que el tenant la elige; el CHECK impide que sea una de las apagadas.</summary>
    public string? DefaultFreightChargeMode { get; set; }
}
