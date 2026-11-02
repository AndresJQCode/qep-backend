using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class WmsUnitOfWork(WmsDbContext dbContext) : IWmsUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The record changed while the update was being committed.",
                exception);
        }
        // Por nombre de índice y no por 23505: dos peticiones a la vez pasan la comprobación previa del handler y sólo la base las
        // distingue, y cada índice dice qué campo hay que corregir.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.WarehouseCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.WarehouseCodeTaken, "A warehouse with this code already exists.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.LocationCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationCodeTaken, "A location with this code already exists in the warehouse.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.OwnerSelfIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.OwnerSelfExists, "The tenant already has a self owner.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.UomPrimaryKey))
        {
            throw new WmsDomainException(WmsErrorCodes.UomCodeTaken, "A unit of measure with this code already exists.", exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.ConversionCodeIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.ProfileConversionDuplicate, "The conversion code is repeated.", exception);
        }
        // Dos peticiones que crean el mismo perfil a la vez: las dos miraron, ninguna vio la fila y las dos arman perfil e identificadores.
        // Sólo una gana. Según el orden del guardado la perdedora tropieza con la clave del perfil o con el índice de identificadores, y
        // ambos son lo mismo: «ya existe, recarga» (412), no un 500 ni un «código ya asignado» que culpa al usuario.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.VariantProfilePrimaryKey))
        {
            throw LostProfileRace(exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.IdentifierValueIndex))
        {
            // Un código de barras que ya es de otra variante también llega acá y ése sí es del usuario: lo distingue que el perfil que
            // esta petición quería crear ya exista en la base.
            if (await AProfileBeingCreatedAlreadyExistsAsync(cancellationToken))
            {
                throw LostProfileRace(exception);
            }

            throw new WmsDomainException(WmsErrorCodes.IdentifierValueTaken, "This identifier is already assigned.", exception);
        }
        // Borrar el padre de una FK RESTRICT da 23001, no 23503: ForeignKeyViolation reconoce las dos.
        catch (DbUpdateException exception) when (ForeignKeyViolation.Is(exception, WmsDbContext.LocationParentForeignKey))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasChildren, "The location still has child locations.", exception);
        }
    }

    private static RequestConcurrencyException LostProfileRace(DbUpdateException exception) =>
        new("concurrency.conflict", "The profile was created by another request while this one was being saved.", exception);

    private async Task<bool> AProfileBeingCreatedAlreadyExistsAsync(CancellationToken cancellationToken)
    {
        var adding = dbContext.ChangeTracker.Entries<VariantLogisticsProfile>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.TenantId, entry.Entity.VariantId))
            .ToList();
        foreach (var (tenantId, variantId) in adding)
        {
            // Sin rastreo: la entidad Added con la misma clave no cuenta, se pregunta a la base.
            if (await dbContext.VariantProfiles.AsNoTracking().AnyAsync(p => p.TenantId == tenantId && p.VariantId == variantId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
