using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure.Persistence;
using Modules.Buyers.Infrastructure.Signup;

namespace Modules.Buyers.Infrastructure;

/// <summary>Cuántas filas borró cada paso de una pasada. Sólo conteos: nada que identifique a un comprador.</summary>
internal sealed record BuyerHousekeepingResult(
    int Accounts,
    int Sessions,
    int Tokens,
    int OutboxMessages = 0,
    int InboxMessages = 0,
    int ProcessedSignups = 0);

/// <summary>
/// Limpieza periódica de datos del comprador, con borrados masivos (nunca se cargan filas):
/// <list type="bullet">
/// <item>cuentas sin verificar cuyo ÚLTIMO registro (<c>updated_at</c>) tiene más de <see cref="BuyerAccount.UnverifiedRetention"/>;
/// el perfil, las sesiones y los tokens se van por cascada;</item>
/// <item>sesiones revocadas, vencidas o inactivas, y tokens usados o vencidos;</item>
/// <item>R10: las filas ya despachadas de <c>platform.outbox_messages</c> de los eventos del comprador con más de
/// <see cref="OutboxRetention"/> (llevan el hash de la contraseña y los tokens en claro), y el inbox y
/// <c>processed_signups</c> con más de <see cref="InboxRetention"/>.</item>
/// </list>
/// <c>buyers.consents</c> NO se toca nunca: el registro de la autorización sobrevive a la cuenta.
///
/// Regla del outbox. <c>processed_at</c> del outbox de plataforma sólo dice que el despachador interno de Tenancy ya pasó el
/// mensaje por sus handlers (los eventos del comprador no tienen ninguno); los consumidores reales (este módulo para el alta,
/// Notifications para los cuatro correos) NO lo miran: eligen candidatos por nombre de evento y llevan su propio inbox por
/// consumidor, en otro esquema. Por eso no sirve como señal de "ya consumido" y este módulo no lee inboxes ajenos. La regla es
/// por edad, con <see cref="OutboxRetention"/> (24 h) muy por encima de la ventana de reintento de un consumidor (lease de 2 min x
/// 3 intentos), y exige además <c>processed_at IS NOT NULL</c> para no borrar nunca un mensaje que la plataforma aún no despachó.
/// </summary>
internal sealed partial class BuyerHousekeepingWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerHousekeepingWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    internal static readonly TimeSpan OutboxRetention = TimeSpan.FromHours(24);

    internal static readonly TimeSpan InboxRetention = TimeSpan.FromDays(30);

    /// <summary>Los eventos del comprador cuyo payload guarda un hash o un token en claro.</summary>
    internal static readonly string[] PurgedOutboxEvents =
    [
        BuyerSignupRequestedMessage.EventName,
        BuyerIntegrationEvents.EmailVerificationRequested,
        BuyerIntegrationEvents.ExistingAccountNotice,
        BuyerIntegrationEvents.PasswordResetRequested,
        BuyerIntegrationEvents.AccountDeleted,
    ];

    [LoggerMessage(Level = LogLevel.Error, Message = "Buyer housekeeping tick failed; the next tick retries.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Buyer housekeeping purged {Accounts} accounts, {Sessions} sessions, {Tokens} tokens, {Outbox} outbox rows, {Inbox} inbox rows and {Signups} processed signups.")]
    private static partial void LogPurged(
        ILogger logger, int accounts, int sessions, int tokens, int outbox, int inbox, int signups);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var result = await RunOnceAsync(now, stoppingToken);
                LogPurged(
                    logger, result.Accounts, result.Sessions, result.Tokens,
                    result.OutboxMessages, result.InboxMessages, result.ProcessedSignups);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    // `internal`: el punto de entrada de las pruebas, sin el PeriodicTimer (precedente OutboxDeliveryWorker.DrainAsync).
    internal async Task<BuyerHousekeepingResult> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BuyersDbContext>();

        // Sesiones y tokens primero: así sus conteos no absorben lo que la cascada de las cuentas se llevaría de todos modos.
        var sessionIdleLimit = now - BuyerSession.IdleTimeout;
        var sessions = await dbContext.Sessions
            .Where(session => session.RevokedAt != null || session.ExpiresAt <= now || session.LastSeenAt <= sessionIdleLimit)
            .ExecuteDeleteAsync(cancellationToken);

        var tokens = await dbContext.EmailTokens
            .Where(token => token.UsedAt != null || token.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var accountLimit = now - BuyerAccount.UnverifiedRetention;
        var accounts = await dbContext.Accounts
            .Where(account => account.EmailVerifiedAt == null && account.UpdatedAt < accountLimit)
            .ExecuteDeleteAsync(cancellationToken);

        var outboxLimit = now - OutboxRetention;
        var outbox = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM platform.outbox_messages
            WHERE event_name = ANY({PurgedOutboxEvents})
              AND processed_at IS NOT NULL
              AND occurred_at < {outboxLimit}
            """,
            cancellationToken);

        var inboxLimit = now - InboxRetention;
        var inbox = await dbContext.Inbox
            .Where(entry => entry.ProcessedAt != null && entry.ProcessedAt < inboxLimit)
            .ExecuteDeleteAsync(cancellationToken);
        var signups = await dbContext.ProcessedSignups
            .Where(signup => signup.ProcessedAt < inboxLimit)
            .ExecuteDeleteAsync(cancellationToken);

        return new BuyerHousekeepingResult(accounts, sessions, tokens, outbox, inbox, signups);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
