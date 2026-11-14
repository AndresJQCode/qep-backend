using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Plan D1 (precedente <c>PipelineProvisioner</c>): Tenancy no avisa cuando se prende un módulo, así que la fila de soporte del
/// tenant y sus tipos se crean en el primer acceso. Una transacción propia con <c>INSERT … ON CONFLICT (tenant_id) DO NOTHING
/// RETURNING tenant_id</c>: dos peticiones a la vez esperan la clave primaria, una gana y la otra no inserta nada; los ocho tipos
/// sólo se siembran si esta llamada creó la fila. SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que el
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
            foreach (var type in SupportTicketTypeDefaults.For(tenantId, now))
            {
                await dbContext.Database.ExecuteSqlAsync($"""
                    INSERT INTO support.ticket_types (id, tenant_id, name, term_business_days, term_is_legal, requires_formal_response,
                        is_published, is_active, position, created_at, updated_at, version)
                    VALUES ({type.Id.Value}, {tenantId}, {type.Name}, {type.TermBusinessDays}, {type.TermIsLegal},
                        {type.RequiresFormalResponse}, {type.IsPublished}, {type.IsActive}, {type.Position}, {now}, {now}, {type.Version})
                    """, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
