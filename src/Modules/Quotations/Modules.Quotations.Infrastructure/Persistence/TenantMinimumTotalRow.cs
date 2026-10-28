namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>A tenant's minimum total in one currency. A missing row means "no money branch for
/// this currency" (fail-closed, spec D7).</summary>
internal sealed class TenantMinimumTotalRow
{
    public Guid TenantId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}
