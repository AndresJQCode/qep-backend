using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure.Persistence;

namespace Modules.Buyers.Infrastructure.Signup;

/// <summary>
/// Crea la cuenta pedida desde el checkout (D7): consume <c>store.buyer-signup-requested.v1</c> del outbox de plataforma con su
/// inbox propio y aplica <see cref="BuyerSignup"/>, el mismo algoritmo del registro, con el hash que trae el mensaje.
///
/// Idempotente por pedido: <c>buyers.processed_signups</c> y el <c>processed_at</c> del inbox entran en el mismo
/// <c>SaveChanges</c> que la cuenta, el perfil, el consentimiento, el token y el outbox del correo. Un mensaje reprocesado (o un
/// segundo mensaje del mismo pedido) sólo cierra su inbox; dos réplicas a la vez chocan en <c>PK_processed_signups</c> y la
/// perdedora descarta lo suyo. Los logs llevan el id del mensaje y nunca el email, el hash ni el token.
/// </summary>
internal sealed partial class BuyerSignupRequestedWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerSignupRequestedWorker> logger) : BackgroundService
{
    internal const string Consumer = "buyers.signup-requested";

    internal const int BatchSize = 20;

    /// <summary>Reclamos sin terminar que se toleran; el siguiente cierra el mensaje sin efecto.</summary>
    internal const int MaxAttempts = 3;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [LoggerMessage(Level = LogLevel.Error, Message = "Buyer signup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Buyer signup message {MessageId} failed; it is retried when its lease expires.")]
    private static partial void LogMessageFailed(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Buyer signup message {MessageId} was claimed {Attempts} times without finishing; it is closed without an account.")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Buyer signup message {MessageId} carries an email that does not normalize; it is closed without an account.")]
    private static partial void LogInvalidEmail(ILogger logger, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Linking store order {OrderId} to its new buyer account failed; the account stands and the order stays unlinked.")]
    private static partial void LogLinkFailed(ILogger logger, Guid orderId, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
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
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // `internal`: el punto de entrada de las pruebas, sin el PeriodicTimer (precedente OutboxDeliveryWorker.DrainAsync).
    internal async Task DrainAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<BuyersDbContext>();
        var clock = services.GetRequiredService<IClock>();

        // Candidatos: sin fila en el inbox, o reclamados y sin terminar con el lease vencido.
        var now = clock.UtcNow;
        var candidates = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == BuyerSignupRequestedMessage.EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer
                && entry.MessageId == record.Id
                && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        foreach (var record in candidates)
        {
            try
            {
                await ProcessAsync(services, dbContext, clock, record, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: al vencer el lease se reintenta, y pasado MaxAttempts se cierra.
                LogMessageFailed(logger, record.Id, exception);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessAsync(
        IServiceProvider services,
        BuyersDbContext dbContext,
        IClock clock,
        OutboxRecord record,
        CancellationToken stoppingToken)
    {
        var attempts = await BuyersInboxClaims.TryClaimAsync(dbContext, Consumer, record.Id, clock.UtcNow, Lease, stoppingToken);
        if (attempts is null)
        {
            // Otra réplica lo tiene, o ya se procesó.
            return;
        }

        if (attempts > MaxAttempts)
        {
            // No se lee el payload: lo que falla puede ser él.
            LogAttemptsExhausted(logger, record.Id, attempts.Value);
            await CloseAsync(dbContext, record.Id, clock.UtcNow);
            return;
        }

        var message = BuyerSignupRequestedMessage.Parse(record.PayloadJson);
        if (!BuyerEmail.TryNormalize(message.Email, out _))
        {
            // No se arregla solo: reintentarlo sólo quemaría los intentos.
            LogInvalidEmail(logger, record.Id);
            await CloseAsync(dbContext, record.Id, clock.UtcNow);
            return;
        }

        if (await dbContext.ProcessedSignups.AnyAsync(
            processed => processed.OrderId == message.OrderId, stoppingToken))
        {
            await CloseAsync(dbContext, record.Id, clock.UtcNow);
            return;
        }

        var profile = new BuyerProfileDto(message.Billing, message.Shipping, message.Shipping is null);
        var request = new BuyerSignupRequest(
            message.StoreHost, message.StoreName, message.VisitorIp, message.Email, message.PasswordHash, profile);
        Guid? accountId;
        try
        {
            accountId = await services.GetRequiredService<BuyerSignup>().SignUpAsync(
                request,
                stoppingToken,
                beforeSave: () => MarkProcessed(dbContext, record.Id, message.OrderId, clock.UtcNow));
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, BuyersDbContext.ProcessedSignupsKey))
        {
            // Otra réplica aplicó el mismo pedido primero: lo pendiente de ésta se descarta y el mensaje queda como procesado.
            dbContext.ChangeTracker.Clear();
            await CloseAsync(dbContext, record.Id, clock.UtcNow);
            return;
        }

        if (accountId is { } created)
        {
            await LinkOrderAsync(services, message, created, stoppingToken);
        }
    }

    // Pedidos del comprador §4.2(2): la cuenta creada o reactivada se pone en el pedido que la pidió, después del commit del alta y
    // fuera de él. Si falla, el alta ya está y el mensaje ya se cerró: queda un aviso y el pedido sin cuenta, sin reintento.
    private async Task LinkOrderAsync(
        IServiceProvider services, BuyerSignupRequestedMessage message, Guid accountId, CancellationToken stoppingToken)
    {
        try
        {
            await services.GetRequiredService<IBuyerOrderLinker>()
                .LinkAsync(message.TenantId, message.OrderId, accountId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLinkFailed(logger, message.OrderId, exception);
        }
    }

    // La fila del inbox la escribió el reclamo en SQL crudo: se adjunta un talón y sólo processed_at queda modificado.
    private static void MarkProcessed(BuyersDbContext dbContext, Guid messageId, Guid orderId, DateTimeOffset now)
    {
        dbContext.ProcessedSignups.Add(new BuyerProcessedSignup { OrderId = orderId, ProcessedAt = now });
        AttachClosed(dbContext, messageId, now);
    }

    private static void AttachClosed(BuyersDbContext dbContext, Guid messageId, DateTimeOffset now)
    {
        var entry = new BuyerInboxMessage { Consumer = Consumer, MessageId = messageId };
        dbContext.Inbox.Attach(entry);
        entry.ProcessedAt = now;
    }

    // El cierre no mira el apagado: el efecto (o la decisión de no tenerlo) ya está tomado.
    private static async Task CloseAsync(BuyersDbContext dbContext, Guid messageId, DateTimeOffset now)
    {
        AttachClosed(dbContext, messageId, now);
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }
}
