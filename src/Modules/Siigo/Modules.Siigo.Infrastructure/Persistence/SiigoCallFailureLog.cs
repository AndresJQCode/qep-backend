using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-21: escribe en <c>siigo.call_failures</c> cada llamada a Siigo que falló. <b>Contexto propio</b>: el del request puede tener
/// cambios a medias que no deben commitearse (el rechazo de una factura se guarda en su fila aparte), y la fila de la falla no se va con una
/// transacción que se revierte. <b>No propaga sus errores</b>: quien lo llama está por devolver el resultado real de Siigo. La expiración
/// no tiene worker: cada escritura borra, de paso, un lote acotado de lo que pasó de <see cref="RetentionDays"/> días, de todos los tenants
/// (el registro es operativo). Sin tenant en el contexto (un llamador que no viene de un request) no hay a quién atar la fila y no escribe.
/// </summary>
internal sealed partial class SiigoCallFailureLog(
    DbContextOptions<SiigoDbContext> options,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<SiigoCallFailureLog> logger) : ISiigoCallFailureLog
{
    /// <summary>Lo que se conserva: la última semana, para lecturas y escrituras por igual (decisión del owner, 2026-11-21).</summary>
    public const int RetentionDays = 7;

    /// <summary>Tope de filas vencidas que borra una escritura: la tabla no crece sin techo y ninguna petición paga un borrado largo.</summary>
    public const int ExpiryBatchSize = 200;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The failed Siigo call ({Operation} {Method}) could not be saved. The result of the call is not affected; the record is lost.")]
    private static partial void LogRecordFailed(ILogger logger, SiigoCallOperation operation, string method, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The expired failed Siigo calls could not be deleted. The new record was saved.")]
    private static partial void LogExpiryFailed(ILogger logger, Exception exception);

    public async Task RecordAsync(SiigoCallFailureReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            if (TenantOf(executionContext) is not { } tenantId)
            {
                return;
            }

            var now = clock.UtcNow;
            await using var dbContext = new SiigoDbContext(options);
            dbContext.CallFailures.Add(SiigoCallFailure.Create(
                tenantId,
                report.ConnectionId,
                report.Operation,
                report.Method,
                report.Path,
                report.Status,
                report.Reason,
                report.IdempotencyKey,
                report.SiigoInvoiceId,
                report.RequestCurl,
                report.ResponseBody,
                SubjectOf(executionContext),
                Activity.Current?.Id,
                now));
            await dbContext.SaveChangesAsync(cancellationToken);

            await ExpireAsync(dbContext, now, cancellationToken);
        }
        // Deliberadamente ancho: se llama con el resultado de Siigo en la mano, y un log caído no lo reemplaza por una excepción.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecordFailed(logger, report.Operation, report.Method, exception);
        }
    }

    private async Task ExpireAsync(SiigoDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = now.AddDays(-RetentionDays);
            await dbContext.CallFailures
                .Where(failure => dbContext.CallFailures
                    .Where(expired => expired.OccurredAt < cutoff)
                    .OrderBy(expired => expired.OccurredAt)
                    .Take(ExpiryBatchSize)
                    .Select(expired => expired.Id)
                    .Contains(failure.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExpiryFailed(logger, exception);
        }
    }

    // El contexto de ejecución del request lanza si no hay claims; un llamador sin request no tiene tenant.
    private static Guid? TenantOf(IExecutionContext context)
    {
        try
        {
            var tenantId = context.TenantId.Value;
            return tenantId == Guid.Empty ? null : tenantId;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Guid? SubjectOf(IExecutionContext context)
    {
        try
        {
            var subjectId = context.SubjectId;
            return subjectId == Guid.Empty ? null : subjectId;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
