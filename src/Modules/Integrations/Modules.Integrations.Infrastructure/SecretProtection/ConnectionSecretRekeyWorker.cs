using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure.SecretProtection;

internal sealed record RekeyRunResult(int Reencrypted, int Skipped);

/// <summary>
/// Re-cifra con la llave activa todo secreto guardado con otra llave que siga configurada (spec
/// 2026-10-08, «Secreto en reposo»; era <c>WhatsAppTokenRekeyWorker</c> en 6612298). Al arrancar y cada
/// <see cref="SecretProtectionOptions.RekeyIntervalMinutes"/>. Rotar es cambiar <c>ActiveKeyId</c> y dejar
/// la vieja declarada: nadie le pide nada al tenant (criterio 4).
/// <list type="bullet">
/// <item>Por corrida lista sólo ids y <c>key_id</c> de lo pendiente; lo procesa en lotes de
/// <see cref="BatchSize"/> conexiones, un guardado por lote (P16).</item>
/// <item>Lo de una llave retirada, lo que no descifra y lo de un lote que chocó con un <c>PUT</c> se
/// salta y se cuenta en <b>una</b> advertencia por corrida, sin valores ni texto cifrado.</item>
/// <item>Idempotente: en la corrida siguiente lo re-cifrado ya no califica.</item>
/// <item>Cualquier otra falla se registra y espera a la próxima corrida: no tumba el host.</item>
/// <item>No audita: no hay una persona detrás y no cambia ningún valor.</item>
/// </list>
/// <see cref="FirstRunCompletion"/> se completa siempre tras la primera corrida: las pruebas la esperan
/// antes de afirmar que algo no cambió.
/// </summary>
internal sealed partial class ConnectionSecretRekeyWorker(
    IServiceScopeFactory scopeFactory,
    ISecretProtector protector,
    IOptions<SecretProtectionOptions> options,
    ILogger<ConnectionSecretRekeyWorker> logger) : BackgroundService
{
    internal const int BatchSize = 100;

    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstRunCompletion => _firstRun.Task;

    internal TimeSpan Interval { get; } = TimeSpan.FromMinutes(options.Value.RekeyIntervalMinutes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Skipped} connection secrets could not be re-encrypted to key {ActiveKeyId} (key retired, ciphertext unreadable or row changed meanwhile); they keep their current key.")]
    private static partial void LogSkipped(ILogger logger, int skipped, string activeKeyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "rekey finished: {Reencrypted} re-encrypted, {Skipped} skipped")]
    private static partial void LogFinished(ILogger logger, int reencrypted, int skipped);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Connection secret rekey failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // StartAsync corre ExecuteAsync hasta el primer await: sin esto, una base lenta frenaría el
            // arranque del host.
            await Task.Yield();
            try
            {
                await RunSafelyAsync(stoppingToken);
            }
            finally
            {
                _firstRun.TrySetResult();
            }

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El host se apaga: no es una falla.
        }
    }

    internal async Task<RekeyRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var active = protector.ActiveKeyId;
        if (active is null)
        {
            return new RekeyRunResult(0, 0);
        }

        List<Guid> connectionIds;
        var skipped = 0;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var pending = await dbContext.Connections
                .AsNoTracking()
                .SelectMany(
                    connection => connection.Secrets,
                    (connection, secret) => new { connection.Id, secret.KeyId })
                .Where(row => row.KeyId != active)
                .ToListAsync(cancellationToken);

            // Llave retirada: no hay con qué descifrar; se cuenta y se deja.
            skipped += pending.Count(row => !protector.HasKey(row.KeyId));
            connectionIds = pending
                .Where(row => protector.HasKey(row.KeyId))
                .Select(row => row.Id)
                .Distinct()
                .ToList();
        }

        var reencrypted = 0;
        foreach (var batch in connectionIds.Chunk(BatchSize))
        {
            var (done, notDone) = await RekeyBatchAsync(batch, active, cancellationToken);
            reencrypted += done;
            skipped += notDone;
        }

        if (skipped > 0)
        {
            LogSkipped(logger, skipped, active);
        }

        LogFinished(logger, reencrypted, skipped);
        return new RekeyRunResult(reencrypted, skipped);
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, exception);
        }
    }

    private async Task<(int Reencrypted, int Skipped)> RekeyBatchAsync(
        Guid[] connectionIds, string active, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var connections = await dbContext.Connections
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToListAsync(cancellationToken);

        var reencrypted = 0;
        var skipped = 0;
        foreach (var connection in connections)
        {
            var stale = connection.Secrets
                .Where(secret => !string.Equals(secret.KeyId, active, StringComparison.Ordinal) && protector.HasKey(secret.KeyId))
                .ToArray();
            foreach (var secret in stale)
            {
                if (protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var plaintext))
                {
                    connection.Reprotect(secret.FieldKey, protector.Protect(connection.Id, secret.FieldKey, plaintext), now);
                    reencrypted++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        if (reencrypted == 0)
        {
            return (0, skipped);
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(cancellationToken);
            return (reencrypted, skipped);
        }
        catch (RequestConcurrencyException)
        {
            // Un PUT guardó una de estas conexiones en el medio: el lote vuelve en la próxima corrida.
            return (0, skipped + reencrypted);
        }
    }
}
