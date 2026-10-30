namespace Modules.Billing.Application;

/// <summary>
/// Dónde queda una fila de la importación que falló por algo que no es una regla de dominio (§3.2b, <c>row.failed</c>): la
/// fila vuelve con un código genérico, así que el log es lo único que dice qué pasó. Puerto propio porque Application no
/// referencia el logging (precedente <c>IQuotationSendFailureLog</c>). Sólo ids y el número de fila: nunca datos personales
/// de la fila.
/// </summary>
public interface IBillingImportFailureLog
{
    void RowFailed(Guid tenantId, int rowNumber, string stage, Exception exception);
}
