using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Infrastructure.Persistence;

internal sealed class SchedulingUnitOfWork(SchedulingDbContext dbContext) : ISchedulingUnitOfWork
{
    public async Task<ISchedulingTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new SchedulingTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core. `version` es el token de concurrencia de toda fila editable (412).
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The scheduling record changed while the update was being committed.",
                exception);
        }
        // El mismo miembro vinculado a dos recursos de una sede por dos peticiones a la vez: la verificación previa del caso de uso no ve
        // la carrera, el índice único sí. Por nombre de índice, no sólo por 23505: hay varios índices únicos y devolver el código equivocado
        // manda a corregir el campo equivocado. Sin esto la carrera sería un 500.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SchedulingDbContext.ResourceMemberUniqueIndex))
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ResourceMemberAlreadyLinked, "Ese miembro ya atiende otro recurso de esta sede.");
        }

        // The same idempotency key booked twice at once on two different resources (on one resource the lock serializes them): the loser
        // gets a 409 and its retry meets the winner's appointment on the first read.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SchedulingDbContext.AppointmentClientRequestIndex))
        {
            throw new ResourceConflictException(
                SchedulingErrorCodes.SlotTaken, "Ya hay una cita en curso con esa llave de petición. Reintenta.", exception);
        }

        // Dos reservas por el mismo tiempo de un recurso: la restricción de exclusión deja pasar a una y rechaza a la otra (D-8). Por nombre
        // de índice, no sólo por 23P01 o 23505.
        catch (DbUpdateException exception) when (
            UniqueViolation.IsExclusion(exception, SchedulingDbContext.SessionOccupancyConstraint)
            || UniqueViolation.Is(exception, SchedulingDbContext.SessionUniqueIndex))
        {
            throw new ResourceConflictException(
                SchedulingErrorCodes.SlotTaken, "Ese espacio ya no está disponible. Elige otro.", exception);
        }
    }

    private sealed class SchedulingTransaction(IDbContextTransaction transaction) : ISchedulingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
