using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Re-cifra con la llave activa, una vez por arranque, toda API key de WhatsApp guardada con otra
/// llave (spec 2026-10-07, «Rotación», punto 3; decisión 26). Sin él, una llave filtrada no salía
/// de circulación hasta que cada tenant guardara su configuración.
/// <list type="bullet">
/// <item>Idempotente: en el siguiente arranque esas filas ya no califican.</item>
/// <item>Una fila que no descifra, o que otro guardado cambió en el medio, se salta y se loguea con
/// tenant id y key id: nunca un valor ni un texto cifrado.</item>
/// <item>Cualquier otra falla se loguea y el worker termina: no tumba el host ni reintenta en
/// bucle.</item>
/// <item>No audita: no hay una persona detrás y no cambia ningún valor de la configuración.</item>
/// </list>
/// <see cref="Completion"/> se completa siempre, haya re-cifrado, saltado o fallado: las pruebas lo
/// esperan antes de afirmar que algo no cambió.
/// </summary>
internal sealed partial class WhatsAppTokenRekeyWorker(
    IServiceScopeFactory scopeFactory,
    IWhatsAppSecretProtector protector,
    ILogger<WhatsAppTokenRekeyWorker> logger) : BackgroundService
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WhatsApp API key of tenant {TenantId} stored with key {KeyId} could not be decrypted; rekey skipped.")]
    private static partial void LogUnreadable(ILogger logger, Guid tenantId, string keyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WhatsApp API key of tenant {TenantId} changed while it was being re-encrypted from key {KeyId}; rekey skipped.")]
    private static partial void LogConflict(ILogger logger, Guid tenantId, string keyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "rekey finished: {Reencrypted} re-encrypted, {Skipped} skipped")]
    private static partial void LogFinished(ILogger logger, int reencrypted, int skipped);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WhatsApp API key rekey failed; it will run again on the next start.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // StartAsync corre ExecuteAsync hasta el primer await: sin esto, una base lenta
            // frenaría el arranque del host.
            await Task.Yield();
            await RekeyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El host se apaga: no es una falla.
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

    private async Task RekeyAsync(CancellationToken cancellationToken)
    {
        var active = protector.ActiveKeyId;
        if (active is null)
        {
            return;
        }

        // Son pocas (una por tenant como mucho): se leen los ids de una vez y cada fila se procesa
        // en su propio scope, para que un conflicto no ensucie el DbContext de las demás.
        List<Guid> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
            pending = await dbContext.WhatsAppSettings
                .AsNoTracking()
                .Where(settings => settings.ApiToken != null && settings.ApiToken.KeyId != active)
                .Select(settings => settings.TenantId)
                .ToListAsync(cancellationToken);
        }

        var reencrypted = 0;
        var skipped = 0;
        foreach (var tenantId in pending)
        {
            if (await RekeyOneAsync(tenantId, active, cancellationToken))
            {
                reencrypted++;
            }
            else
            {
                skipped++;
            }
        }

        LogFinished(logger, reencrypted, skipped);
    }

    private async Task<bool> RekeyOneAsync(Guid tenantId, string active, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>();
        var settings = await repository.FindAsync(tenantId, cancellationToken);
        var current = settings?.ApiToken;
        if (settings is null || current is null ||
            string.Equals(current.KeyId, active, StringComparison.Ordinal))
        {
            // Otro guardado o el worker de otra réplica ya la dejó bien entre la lista y esta lectura.
            LogConflict(logger, tenantId, current?.KeyId ?? "(none)");
            return false;
        }

        if (!protector.TryUnprotect(tenantId, current, out var plaintext))
        {
            LogUnreadable(logger, tenantId, current.KeyId);
            return false;
        }

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        settings.Reprotect(protector.Protect(tenantId, plaintext!), clock.UtcNow);
        try
        {
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (RequestConcurrencyException)
        {
            // Alguien guardó la fila en el medio: el PUT ya la dejó bien o la deja el próximo arranque.
            LogConflict(logger, tenantId, current.KeyId);
            return false;
        }
    }
}
