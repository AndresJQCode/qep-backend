using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// El único escritor del Self y de las seis unidades de sistema (S5). Tenancy no avisa cuando se activa un módulo, así que se
/// provisiona en el primer acceso: una transacción propia con <c>INSERT … ON CONFLICT DO NOTHING</c>, y dos peticiones a la vez
/// esperan el índice, una inserta y la otra no hace nada. SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que
/// el contexto ya tuviera rastreados. Copia de <c>PipelineProvisioner</c>.
/// </summary>
internal sealed class WmsProvisioner(WmsDbContext dbContext, IClock clock) : IWmsProvisioner
{
    private const string SelfName = "Propio";

    public async Task EnsureAsync(Guid tenantId, CancellationToken ct)
    {
        var defaults = UnitOfMeasure.SystemDefaults(tenantId, clock.UtcNow);

        // El camino caliente: casi todas las llamadas son de un tenant ya provisionado y no hace falta abrir transacción.
        var hasSelf = await dbContext.Owners.AsNoTracking()
            .AnyAsync(owner => owner.TenantId == tenantId && owner.Kind == OwnerKind.Self, ct);
        var systemUnits = await dbContext.UnitsOfMeasure.AsNoTracking()
            .CountAsync(uom => uom.TenantId == tenantId && uom.IsSystem, ct);
        if (hasSelf && systemUnits >= defaults.Count)
        {
            return;
        }

        var now = clock.UtcNow;
        var self = Owner.CreateSelf(OwnerId.New(), tenantId, SelfName, now);
        // El consumidor de inventario básico provisiona dentro de su transacción (spec §5.3): con una abierta, escribe en ella y es
        // quien la abrió quien confirma. Sin ninguna, ésta es la propia.
        var ambient = dbContext.Database.CurrentTransaction;
        await using var transaction = ambient is null ? await dbContext.Database.BeginTransactionAsync(ct) : null;

        await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO wms.owners (id, tenant_id, kind, name, identification, contact_email, is_active, version, created_at, updated_at)
            VALUES ({self.Id.Value}, {tenantId}, {self.Kind.ToString()}, {self.Name}, NULL, NULL, TRUE, {self.Version}, {now}, {now})
            ON CONFLICT (tenant_id) WHERE kind = 'Self' DO NOTHING
            """, ct);

        foreach (var uom in defaults)
        {
            await dbContext.Database.ExecuteSqlAsync($"""
                INSERT INTO wms.units_of_measure (tenant_id, code, name, precision, is_system, created_at)
                VALUES ({tenantId}, {uom.Code.Value}, {uom.Name}, {uom.Precision}, TRUE, {now})
                ON CONFLICT (tenant_id, code) DO NOTHING
                """, ct);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }
}
