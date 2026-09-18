namespace Modules.Quotations.Application;

/// <summary>
/// Emite el próximo consecutivo del número de cotización de un tenant para un año dado. Mismo
/// mecanismo que <c>ICucGenerator</c> en Customers: un contador atómico por tenant (acá, también
/// por año) resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. El formato final lo arma
/// <c>DocumentNumberFormatter</c> con el formato del tenant — este puerto sólo resuelve la
/// concurrencia del consecutivo.
///
/// El <c>year</c> es el del día del tenant cuando el formato lleva año, y <b>0</b> cuando no lo
/// lleva (spec 2026-09-17): la fila <c>year = 0</c> es el contador que no se reinicia.
/// </summary>
public interface IQuotationNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken);
}
