namespace Modules.Quotations.Application;

/// <summary>Mismo mecanismo que <see cref="IQuotationNumberGenerator"/>: un contador atómico por
/// (tenant, año), resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure.</summary>
public interface IOrderNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken);
}
