using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain;

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
                "The warehouse record changed while the update was being committed.",
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
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, WmsDbContext.IdentifierValueIndex))
        {
            throw new WmsDomainException(WmsErrorCodes.IdentifierValueTaken, "This identifier is already assigned.", exception);
        }
        // Borrar el padre de una FK RESTRICT da 23001, no 23503: ForeignKeyViolation reconoce las dos.
        catch (DbUpdateException exception) when (ForeignKeyViolation.Is(exception, WmsDbContext.LocationParentForeignKey))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasChildren, "The location still has child locations.", exception);
        }
    }
}
