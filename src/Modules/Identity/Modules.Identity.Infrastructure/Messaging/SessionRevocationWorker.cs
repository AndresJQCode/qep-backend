using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Messaging;

// Consume del Outbox de plataforma los eventos de integración de membresía suspendida o
// quitada y revoca toda sesión activa del usuario afectado. Es idempotente por el inbox
// propio de este módulo, con clave (consumidor, id de mensaje de outbox): un mensaje
// reentregado se saltea. Session.Revoke es idempotente en sí mismo, así que procesar dos
// veces tras una caída también es seguro, no sólo la entrega única.
//
// Deliberadamente revoca TODAS las sesiones del usuario, no sólo las del tenant afectado:
// el token de sesión no lleva contexto de tenant (tenant y permisos se resuelven en vivo
// por request desde el estado de la membresía, ver ExternalClaimsTransformation), así que
// no hay sesión por tenant a la cual acotar esto. Desloguear al usuario de todos los
// tenants ante una suspensión/baja de un solo tenant es más amplio que lo estrictamente
// necesario, pero más simple y seguro — ver el ADR de cookie de sesión por el trade-off.
internal sealed partial class SessionRevocationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionRevocationWorker> logger) : BackgroundService
{
    private const string Consumer = "identity.session-revocation";
    private const string SuspendedEvent = "tenancy.membership-suspended.v1";
    private const string RemovedEvent = "tenancy.membership-removed.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session revocation tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
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

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == SuspendedEvent || record.EventName == RemovedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await RevokeAsync(dbContext, record, cancellationToken);
        }
    }

    private static async Task RevokeAsync(
        IdentityDbContext dbContext,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var userId = ParsePayload(record.PayloadJson);
        var reason = record.EventName == SuspendedEvent
            ? "membership_suspended"
            : "membership_removed";
        var now = DateTimeOffset.UtcNow;

        var activeSessions = await dbContext.Sessions
            .Where(session => session.UserId == new Modules.Identity.Domain.UserId(userId)
                && session.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in activeSessions)
        {
            session.Revoke(now, reason);
            dbContext.AuditEntries.Add(AuditEntry.Create(
                tenantId: null,
                userId,
                AuditActorType.System,
                "identity.session.revoked",
                "session",
                session.Id.ToString(),
                "success",
                "[]",
                "identity",
                now));
        }

        dbContext.Inbox.Add(new IdentityInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Guid ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("userId").GetGuid();
    }
}
