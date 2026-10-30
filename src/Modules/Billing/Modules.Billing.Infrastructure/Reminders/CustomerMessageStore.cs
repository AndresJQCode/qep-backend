using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Billing.Infrastructure.Persistence;

namespace Modules.Billing.Infrastructure.Reminders;

/// <summary>
/// El mensaje del día a un cliente y los pasos que cubre (spec §2, §3.3 paso 4). Reclamar es insertar: el único
/// <c>(tenant, customer, run_date)</c> y la PK <c>(charge_id, step)</c> son la deduplicación entre réplicas.
/// </summary>
internal sealed class CustomerMessageStore(BillingDbContext dbContext)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // Spec §3.4: el código va en varchar(64); un `send_failed:<code>` de un proveedor no puede romper el guardado del fallo.
    private const int FailureCodeMaxLength = 64;

    /// <summary>
    /// Inserta la fila <c>Sending</c> y una fila de <c>reminder_steps</c> por paso en un solo <c>SaveChanges</c>. Null: otra réplica
    /// ganó el día del cliente o alguno de los pasos, y no se manda nada.
    /// </summary>
    public async Task<Guid?> TryClaimAsync(
        Guid tenantId,
        Guid customerId,
        DateOnly runDate,
        BillingMessageKind kind,
        BillingChannel channel,
        IReadOnlyList<(Guid ChargeId, string Step)> steps,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var message = new CustomerMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CustomerId = customerId,
            RunDate = runDate,
            Kind = kind == BillingMessageKind.Overdue ? CustomerMessageKind.Overdue : CustomerMessageKind.Reminder,
            Channel = channel,
            Status = MessageDeliveryStatus.Sending,
            ClaimedUntil = now + Lease,
            AttemptedAt = now,
        };
        dbContext.CustomerMessages.Add(message);
        dbContext.ReminderSteps.AddRange(steps.Select(step => new ReminderStep
        {
            ChargeId = step.ChargeId,
            Step = step.Step,
            MessageId = message.Id,
            TenantId = tenantId,
        }));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return message.Id;
        }
        catch (DbUpdateException exception)
            when (UniqueViolation.Is(exception, "IX_customer_messages_tenant_customer_day", "PK_reminder_steps"))
        {
            return null;
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Retoma una fila <c>Sending</c> con el lease vencido (la réplica que la reclamó se cayó). Se reintenta <b>una</b> vez (spec §2):
    /// la retoma deja <c>send_failed</c> puesto, así que si este intento también muere la fila ya dice cómo terminó y
    /// <see cref="FailAbandonedAsync"/> la cierra. Un envío exitoso lo borra.
    /// </summary>
    public async Task<bool> TryRetakeAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.customer_messages
            SET claimed_until = {now + Lease}, attempted_at = {now}, failure_code = 'send_failed'
            WHERE id = {messageId} AND status = 'Sending' AND claimed_until < {now} AND failure_code IS NULL
            """,
            cancellationToken) == 1;

    /// <summary>Una fila que ya tuvo su reintento y volvió a quedar colgada pasa a <c>Failed</c> con el código que dejó la retoma.</summary>
    public Task FailAbandonedAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.customer_messages SET status = 'Failed'
            WHERE id = {messageId} AND status = 'Sending' AND claimed_until < {now} AND failure_code IS NOT NULL
            """,
            cancellationToken);

    public Task MarkSentAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.customer_messages SET status = 'Sent', sent_at = {now}, failure_code = NULL
            WHERE id = {messageId} AND status = 'Sending'
            """,
            cancellationToken);

    public Task MarkFailedAsync(Guid messageId, string failureCode, CancellationToken cancellationToken)
    {
        var code = failureCode.Length > FailureCodeMaxLength ? failureCode[..FailureCodeMaxLength] : failureCode;
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE billing.customer_messages SET status = 'Failed', failure_code = {code}
            WHERE id = {messageId} AND status = 'Sending'
            """,
            cancellationToken);
    }
}
