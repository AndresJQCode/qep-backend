using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

public enum ExportJobRunOutcome
{
    NoJob,
    Completed,
    RetryScheduled,
    Failed,
    LeaseLost,
}

/// <summary>
/// Un tick del worker de exportaciones: toma un job, lo despacha a su procesador y lo cierra.
/// Vive en Application y no en el worker para poder probar la clasificación de fallos sin
/// Postgres ni temporizadores; el worker sólo lo llama en un scope nuevo por job.
/// </summary>
public sealed class ExportJobRunner(
    IExportJobQueue queue,
    IEnumerable<IExportJobProcessor> processors,
    IExportEventPublisher eventPublisher,
    IQuotationAuditPublisher auditPublisher,
    IQuotationsUnitOfWork unitOfWork,
    IClock clock)
{
    // Un diccionario armado al construir: dos procesadores del mismo kind son un error de
    // cableado, y así explota el tick (que lo loguea) en vez de elegir uno en silencio.
    private readonly Dictionary<ExportJobKind, IExportJobProcessor> _processors =
        processors.ToDictionary(processor => processor.Kind);

    public async Task<ExportJobRunOutcome> RunNextAsync(CancellationToken cancellationToken)
    {
        var job = await queue.ClaimNextAsync(clock.UtcNow, cancellationToken);
        if (job is null)
        {
            return ExportJobRunOutcome.NoJob;
        }

        if (job.HasExceededAttempts)
        {
            return await FailAsync(
                job, "LeaseExpired: the worker stopped during the last attempt.", cancellationToken);
        }

        if (!_processors.TryGetValue(job.Kind, out var processor))
        {
            return await FailAsync(
                job, $"NoProcessor: no export processor is registered for '{job.Kind}'.", cancellationToken);
        }

        ExportJobResult result;
        try
        {
            result = await processor.ProcessAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado del proceso, no fallo del job: queda en Processing y el lease lo devuelve.
            throw;
        }
        catch (ExportJobDefinitiveException exception)
        {
            return await FailAsync(job, Describe(exception), cancellationToken);
        }
        catch (Exception exception)
        {
            var now = clock.UtcNow;
            var failed = job.RecordTransientFailure(Describe(exception), now);
            if (failed)
            {
                eventPublisher.PublishFailed(job, now);
            }

            return await SaveAsync(
                failed ? ExportJobRunOutcome.Failed : ExportJobRunOutcome.RetryScheduled,
                cancellationToken);
        }

        // D10: estado, evento y auditoría en un solo guardado. Si este guardado falla por otra
        // cosa que el lease, la excepción sube al worker: el scope se descarta con lo que
        // tenía trackeado —nada de reusarlo para registrar un reintento con el evento de
        // "listo" adentro— y el lease vencido devuelve el job a la cola.
        var finishedAt = clock.UtcNow;
        job.Complete(result.FileName, result.RowCount, finishedAt);
        eventPublisher.PublishReady(job, result, finishedAt);
        auditPublisher.Publish(
            job.TenantId,
            job.RequestedBy,
            AuditActionFor(job.Kind),
            job.Id.ToString("D", CultureInfo.InvariantCulture),
            $"success:{result.RowCount}",
            finishedAt);
        return await SaveAsync(ExportJobRunOutcome.Completed, cancellationToken);
    }

    /// <summary>D13: lo terminado hace más de <see cref="ExportJob.Retention"/>.</summary>
    public Task<int> PurgeFinishedAsync(CancellationToken cancellationToken) =>
        queue.PurgeFinishedBeforeAsync(clock.UtcNow - ExportJob.Retention, cancellationToken);

    private async Task<ExportJobRunOutcome> FailAsync(
        ExportJob job, string error, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        job.Fail(error, now);
        eventPublisher.PublishFailed(job, now);
        return await SaveAsync(ExportJobRunOutcome.Failed, cancellationToken);
    }

    // `attempts` es token de concurrencia: si otro worker retomó el job con el lease vencido, el
    // UPDATE no encuentra la fila y QuotationsUnitOfWork lo traduce a RequestConcurrencyException.
    // El job es del otro; éste no manda nada.
    private async Task<ExportJobRunOutcome> SaveAsync(
        ExportJobRunOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return outcome;
        }
        catch (RequestConcurrencyException)
        {
            return ExportJobRunOutcome.LeaseLost;
        }
    }

    private static string AuditActionFor(ExportJobKind kind) => kind switch
    {
        ExportJobKind.Sales => "quotation.sale.exported",
        _ => "quotation.quotation.exported",
    };

    // Sólo tipo y mensaje (D11): last_error lo lee soporte, y un stack trace no le suma nada.
    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}
