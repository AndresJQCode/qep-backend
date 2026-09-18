namespace Modules.Quotations.Application;

/// <summary>Mismo mecanismo que <see cref="IQuotationNumberGenerator"/>: un contador atómico por
/// (tenant, año), resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. Un formato sin año
/// pide el consecutivo con <c>year = 0</c> (spec 2026-09-17).</summary>
public interface IOrderNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken);
}
