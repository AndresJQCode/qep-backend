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

/// <summary>Cuántas filas borró cada paso de una pasada, y cuántos pasos fallaron. Sólo conteos: nada que identifique a un
/// comprador. Un paso que falló cuenta 0 filas.</summary>
internal sealed record BuyerHousekeepingResult(
    int Accounts,
    int Sessions,
    int Tokens,
    int OutboxMessages = 0,
    int InboxMessages = 0,
    int ProcessedSignups = 0,
    int FailedSteps = 0);

/// <summary>
/// Limpieza periódica de datos del comprador, con borrados masivos (nunca se cargan filas):
/// <list type="bullet">
/// <item>cuentas sin verificar cuyo ÚLTIMO registro o reenvío de la verificación (<c>updated_at</c>) tiene más de
/// <see cref="BuyerAccount.UnverifiedRetention"/>; el perfil, las sesiones y los tokens se van por cascada;</item>
/// <item>sesiones revocadas, vencidas o inactivas, y tokens usados o vencidos;</item>
/// <item>R10: las filas de <c>platform.outbox_messages</c> de los eventos del comprador (llevan el hash de la contraseña y los
/// tokens en claro), y el inbox y <c>processed_signups</c> con más de <see cref="InboxRetention"/>.</item>
/// </list>
/// <c>buyers.consents</c> NO se toca nunca: el registro de la autorización sobrevive a la cuenta.
///
/// Regla del outbox. <c>processed_at</c> del outbox de plataforma sólo dice que el despachador interno de Tenancy ya pasó el
/// mensaje por sus handlers (los eventos del comprador no tienen ninguno); los consumidores reales no lo miran, así que no se usa
/// como señal (T13a: atarlo a la salud de ese despachador dejaba vivos los tokens si se trababa).
/// <list type="bullet">
/// <item>Los cuatro correos: sólo por edad, más de <see cref="OutboxRetention"/> (24 h), muy por encima de la ventana de reintento
/// de Notifications (lease de 2 min x 3 intentos). Su inbox es de otro esquema y este módulo no lo lee.</item>
/// <item>El alta (T13c): sólo cuando <c>buyers.inbox_messages</c> tiene la fila PROCESADA del consumidor
/// <see cref="BuyerSignupRequestedWorker.Consumer"/> para ese mensaje. La edad sola perdería un alta si el worker estuvo caído
/// más de un día.</item>
/// </list>
/// Cada paso corre aislado (T13b): uno que falla se registra (con el tipo de la excepción, nunca su mensaje, que puede traer
/// valores de filas) y los demás siguen.
/// </summary>
internal sealed partial class BuyerHousekeepingWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerHousekeepingWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    internal static readonly TimeSpan OutboxRetention = TimeSpan.FromHours(24);

    internal static readonly TimeSpan InboxRetention = TimeSpan.FromDays(30);

    /// <summary>Los correos del comprador: su payload guarda un token en claro o el correo. Se purgan por edad.</summary>
    internal static readonly string[] PurgedEmailEvents =
    [
        BuyerIntegrationEvents.EmailVerificationRequested,
        BuyerIntegrationEvents.ExistingAccountNotice,
        BuyerIntegrationEvents.PasswordResetRequested,
        BuyerIntegrationEvents.AccountDeleted,
    ];

    [LoggerMessage(Level = LogLevel.Error, Message = "Buyer housekeeping tick failed; the next tick retries.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Buyer housekeeping step {Step} failed with {ExceptionType}; the other steps still ran and the next tick retries it.")]
    private static partial void LogStepFailed(ILogger logger, string step, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Buyer housekeeping purged {Accounts} accounts, {Sessions} sessions, {Tokens} tokens, {Outbox} outbox rows, {Inbox} inbox rows and {Signups} processed signups; {Failed} steps failed.")]
    private static partial void LogPurged(
        ILogger logger, int accounts, int sessions, int tokens, int outbox, int inbox, int signups, int failed);

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
                    result.OutboxMessages, result.InboxMessages, result.ProcessedSignups, result.FailedSteps);
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
        var failed = 0;

        async Task<int> StepAsync(string step, Func<Task<int>> run)
        {
            try
            {
                return await run();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                LogStepFailed(logger, step, exception.GetType().Name);
                return 0;
            }
        }

        // Sesiones y tokens primero: así sus conteos no absorben lo que la cascada de las cuentas se llevaría de todos modos.
        var sessionIdleLimit = now - BuyerSession.IdleTimeout;
        var sessions = await StepAsync("sessions", () => dbContext.Sessions
            .Where(session => session.RevokedAt != null || session.ExpiresAt <= now || session.LastSeenAt <= sessionIdleLimit)
            .ExecuteDeleteAsync(cancellationToken));

        var tokens = await StepAsync("email-tokens", () => dbContext.EmailTokens
            .Where(token => token.UsedAt != null || token.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken));

        var accountLimit = now - BuyerAccount.UnverifiedRetention;
        var accounts = await StepAsync("accounts", () => dbContext.Accounts
            .Where(account => account.EmailVerifiedAt == null && account.UpdatedAt < accountLimit)
            .ExecuteDeleteAsync(cancellationToken));

        var outboxLimit = now - OutboxRetention;
        var emailOutbox = await StepAsync("outbox-emails", () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM platform.outbox_messages
            WHERE event_name = ANY({PurgedEmailEvents})
              AND occurred_at < {outboxLimit}
            """,
            cancellationToken));

        var signupOutbox = await StepAsync("outbox-signups", () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM platform.outbox_messages AS outbox
            WHERE outbox.event_name = {BuyerSignupRequestedMessage.EventName}
              AND EXISTS (
                  SELECT 1 FROM buyers.inbox_messages AS inbox
                  WHERE inbox.consumer = {BuyerSignupRequestedWorker.Consumer}
                    AND inbox.message_id = outbox.id
                    AND inbox.processed_at IS NOT NULL)
            """,
            cancellationToken));

        // Va después del outbox y nunca borra la fila del inbox de un mensaje que sigue en el outbox: sin ella, el worker de alta
        // tomaría el mensaje como nuevo y lo volvería a procesar. Así, si el paso del outbox falló (o el mensaje todavía no se
        // purgó), el inbox espera a una pasada en la que el mensaje ya no esté.
        var inboxLimit = now - InboxRetention;
        var inbox = await StepAsync("inbox", () => dbContext.Inbox
            .Where(entry => entry.ProcessedAt != null
                && entry.ProcessedAt < inboxLimit
                && !dbContext.Outbox.Any(record => record.Id == entry.MessageId))
            .ExecuteDeleteAsync(cancellationToken));

        // processed_signups (por orderRequestId) es la segunda línea contra un alta repetida; la primera es la fila del inbox de
        // arriba, que sigue viva mientras su mensaje exista. Por eso basta la edad.
        var signups = await StepAsync("processed-signups", () => dbContext.ProcessedSignups
            .Where(signup => signup.ProcessedAt < inboxLimit)
            .ExecuteDeleteAsync(cancellationToken));

        return new BuyerHousekeepingResult(
            accounts, sessions, tokens, emailOutbox + signupOutbox, inbox, signups, failed);
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
