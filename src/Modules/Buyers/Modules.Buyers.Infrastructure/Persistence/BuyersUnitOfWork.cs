using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, nunca por <c>SqlState</c> solo. Vive acá y no en Application,
/// que no referencia EF Core.
/// </summary>
internal sealed class BuyersUnitOfWork(BuyersDbContext dbContext) : IBuyersUnitOfWork
{
    internal const string EmailIndex = "IX_accounts_email";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, EmailIndex))
        {
            // Dos registros del mismo email a la vez: el índice decide el ganador. Sin limpiar el tracker la cuenta
            // perdedora sigue Added y el siguiente SaveChanges del scope la reintenta.
            dbContext.ChangeTracker.Clear();
            throw new ResourceConflictException(
                BuyerErrorCodes.EmailTaken,
                "Another buyer account already uses that email.",
                exception);
        }
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();
}
