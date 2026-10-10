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
        // Keyset por id: cada lote empieza después del último id mirado. Las que no parsean quedan atrás
        // (siguen con phone_e164 NULL) sin volver a traerse en el mismo arranque, y cada lote cuesta lo
        // mismo: con un Take que creciera con las saltadas, el costo sería cuadrático.
        var lastId = Guid.Empty;
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            // El id se compara en SQL: CustomerId no tiene operador de orden, y el orden de uuid en Postgres
            // es el mismo que usa el ORDER BY, así que el cursor no se salta ni repite filas.
            var ids = await dbContext.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM customers.customers
                 WHERE phone IS NOT NULL AND phone_e164 IS NULL AND id > {lastId}
                 ORDER BY id
                 LIMIT {BatchSize}
                """).ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            lastId = ids[^1];
            var keys = ids.Select(id => new CustomerId(id)).ToList();
            var pending = await dbContext.Customers
                .Where(customer => keys.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var customer in pending)
            {
                if (customer.RecomputePhoneE164(normalizer))
                {
                    updated++;
                }
                else
                {
                    unparseable++;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        LogFinished(logger, updated, unparseable);
    }
}
