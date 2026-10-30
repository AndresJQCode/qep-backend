using Microsoft.Extensions.Logging;
using Modules.Billing.Application;

namespace Modules.Billing.Infrastructure;

/// <summary>Ver <see cref="IBillingImportFailureLog"/>.</summary>
internal sealed partial class BillingImportFailureLog(ILogger<BillingImportFailureLog> logger) : IBillingImportFailureLog
{
    // El mensaje lleva sólo ids, el número de fila y el paso; la excepción va aparte, como siempre.
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Billing import row {RowNumber} of tenant {TenantId} failed at {Stage}; it was reported as row.failed.")]
    private static partial void LogRowFailed(ILogger logger, int rowNumber, Guid tenantId, string stage, Exception exception);

    public void RowFailed(Guid tenantId, int rowNumber, string stage, Exception exception) =>
        LogRowFailed(logger, rowNumber, tenantId, stage, exception);
}
