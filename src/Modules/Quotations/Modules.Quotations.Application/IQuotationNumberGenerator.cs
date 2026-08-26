namespace Modules.Quotations.Application;

/// <summary>
/// Emite el próximo consecutivo del número de cotización de un tenant para un año dado. Mismo
/// mecanismo que <c>ICucGenerator</c> en Customers: un contador atómico por tenant (acá, también
/// por año) resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. El formato final
/// (<c>QUO-2026-0001</c>) lo arma <see cref="QuotationNumberFormatter"/> — este puerto sólo
/// resuelve la concurrencia del consecutivo.
/// </summary>
public interface IQuotationNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken);
}
