using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-21, «Aprovisionamiento»: Tenancy no avisa cuando se activa un módulo (H1 del embudo), así que la sede principal y las reglas
/// se crean en el primer acceso. Una transacción propia con <c>INSERT … ON CONFLICT DO NOTHING</c>: dos peticiones a la vez esperan el
/// índice único, una gana y la otra no inserta nada. SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que el contexto ya
/// tuviera rastreados. Calco de <c>PipelineProvisioner</c>.
/// </summary>
internal sealed class SchedulingProvisioner(SchedulingDbContext dbContext, IClock clock) : ISchedulingProvisioner
{
    public async Task EnsureAsync(Guid tenantId, string mainLocationName, CancellationToken cancellationToken)
    {
        // Las reglas se insertan en la misma transacción que la sede: si existen, la sede también.
        if (await dbContext.Settings.AsNoTracking().AnyAsync(settings => settings.TenantId == tenantId, cancellationToken))
        {
            return;
        }

        var now = clock.UtcNow;
        var location = Location.Create(LocationId.New(), tenantId, mainLocationName, isMain: true, now);
        var rules = SchedulingSettings.Create(tenantId, now);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO scheduling.locations (id, tenant_id, name, is_main, archived_at, version, created_at, updated_at)
            VALUES ({location.Id.Value}, {tenantId}, {location.Name}, TRUE, NULL, {location.Version}, {now}, {now})
            ON CONFLICT (tenant_id) WHERE is_main DO NOTHING
            """, cancellationToken);
        await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO scheduling.settings (
                tenant_id, confirmation_mode, min_notice_minutes, horizon_days, client_cancel_cutoff_hours, reminder_enabled,
                reminder_offset_hours, reminder_connection_id, version, created_at, updated_at)
            VALUES (
                {tenantId}, {rules.ConfirmationMode.ToString()}, {rules.MinNoticeMinutes}, {rules.HorizonDays}, {rules.ClientCancelCutoffHours},
                {rules.ReminderEnabled}, {rules.ReminderOffsetHours}, NULL, {rules.Version}, {now}, {now})
            ON CONFLICT (tenant_id) DO NOTHING
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
