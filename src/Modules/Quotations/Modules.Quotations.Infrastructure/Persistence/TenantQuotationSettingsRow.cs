namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>A tenant's minimum-purchase units (spec D7). A persistence row, not an aggregate:
/// it has no behaviour of its own; QuotationSettings.Create is where the rules live.</summary>
internal sealed class TenantQuotationSettingsRow
{
    public Guid TenantId { get; set; }

    public int MinimumUnits { get; set; }
}
