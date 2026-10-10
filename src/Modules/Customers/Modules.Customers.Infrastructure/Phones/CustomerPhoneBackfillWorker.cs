using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;

namespace Modules.Customers.Infrastructure.Phones;

/// <summary>
/// Spec 2026-10-09 §6.5: al arrancar, recorre en lotes de 500 las filas con <c>phone IS NOT NULL AND
/// phone_e164 IS NULL</c> y las recalcula. Idempotente: las que no parsean vuelven a intentarse en cada
/// arranque; son pocas. Sin versión ni <c>updated_at</c>: nadie editó al cliente.
/// </summary>
internal sealed partial class CustomerPhoneBackfillWorker(
    IServiceScopeFactory scopeFactory,
    IPhoneNumberNormalizer normalizer,
    ILogger<CustomerPhoneBackfillWorker> logger) : BackgroundService
{
    internal const int BatchSize = 500;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer phone backfill finished: {Updated} updated, {Unparseable} still without phone_e164.")]
    private static partial void LogFinished(ILogger logger, int updated, int unparseable);

    [LoggerMessage(Level = LogLevel.Error, Message = "Customer phone backfill failed; it runs again on the next start.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var updated = 0;
        var unparseable = 0;
        // Los ids ya mirados que siguen sin E.164 se saltan en el mismo arranque: si no, el lote
        // siguiente los volvería a traer para siempre.
        var skipped = new HashSet<Guid>();
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            var batch = await dbContext.Customers
                .Where(customer => customer.Phone != null && customer.PhoneE164 == null)
                .OrderBy(customer => customer.Id)
                .Take(BatchSize + skipped.Count)
                .ToListAsync(cancellationToken);
            var pending = batch.Where(customer => !skipped.Contains(customer.Id.Value)).Take(BatchSize).ToList();
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var customer in pending)
            {
                if (customer.RecomputePhoneE164(normalizer))
                {
                    updated++;
                }
                else
                {
                    unparseable++;
                    skipped.Add(customer.Id.Value);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        LogFinished(logger, updated, unparseable);
    }
}
