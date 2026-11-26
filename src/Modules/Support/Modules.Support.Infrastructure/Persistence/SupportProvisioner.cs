using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Plan D1 (precedente <c>PipelineProvisioner</c>): Tenancy no avisa cuando se prende un módulo, así que la fila de soporte del
/// tenant y sus tipos se crean en el primer acceso. Una transacción propia con <c>INSERT … ON CONFLICT (tenant_id) DO NOTHING
/// RETURNING tenant_id</c>: dos peticiones a la vez esperan la clave primaria, una gana y la otra no inserta nada; los ocho tipos
/// (y el interno, spec 2026-11-22) sólo se siembran si esta llamada creó la fila. SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que el
/// contexto ya tuviera rastreados.
/// </summary>
internal sealed class SupportProvisioner(SupportDbContext dbContext, IClock clock) : ISupportProvisioner
{
    public async Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await dbContext.TenantSettings.AsNoTracking().AnyAsync(settings => settings.TenantId == tenantId, cancellationToken))
        {
            return;
        }

        var now = clock.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await dbContext.Database.SqlQuery<Guid>($"""
            INSERT INTO support.tenant_settings (tenant_id, created_at, updated_at)
            VALUES ({tenantId}, {now}, {now})
            ON CONFLICT (tenant_id) DO NOTHING
            RETURNING tenant_id AS "Value"
            """).ToListAsync(cancellationToken);
        if (inserted.Count == 1)
        {
            var types = SupportTicketTypeDefaults.For(tenantId, now);
            foreach (var type in types)
            {
                await InsertTypeAsync(type, now, cancellationToken);
            }

            // El noveno, detrás de los ocho: las posiciones de los externos no se mueven.
            await InsertTypeAsync(SupportTicketTypeDefaults.Internal(tenantId, types.Count + 1, now), now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureInternalTypeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Detrás del último tipo del tenant; el índice único parcial hace de candado si dos llamadas corren a la vez.
        await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO support.ticket_types (id, tenant_id, name, term_is_legal, requires_formal_response, is_published, is_active,
                is_internal, position, created_at, updated_at, version)
            SELECT {Guid.CreateVersion7()}, {tenantId}, {SupportTicketTypeDefaults.InternalTaskName}, false, false, false, true, true,
                COALESCE((SELECT max(position) FROM support.ticket_types WHERE tenant_id = {tenantId}), 0) + 1, {now}, {now}, 1
            ON CONFLICT (tenant_id) WHERE is_internal DO NOTHING
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<int> InsertTypeAsync(SupportTicketType type, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO support.ticket_types (id, tenant_id, name, term_business_days, term_is_legal, requires_formal_response,
                is_published, is_active, is_internal, position, created_at, updated_at, version)
            VALUES ({type.Id.Value}, {type.TenantId}, {type.Name}, {type.TermBusinessDays}, {type.TermIsLegal},
                {type.RequiresFormalResponse}, {type.IsPublished}, {type.IsActive}, {type.IsInternal}, {type.Position}, {now}, {now}, {type.Version})
            """, cancellationToken);
}
