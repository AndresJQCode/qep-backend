namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>El consecutivo del número de venta de un tenant, por año -- mismo criterio que
/// <see cref="QuotationNumberCounter"/>.</summary>
internal sealed class SaleNumberCounter
{
    public Guid TenantId { get; init; }

    public int Year { get; init; }

    public long NextValue { get; init; }
}
