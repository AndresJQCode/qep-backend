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

    internal const string ConcurrencyConflictCode = "concurrency.conflict";

    /// <summary>Las claves foráneas hacia la cuenta: insertar un hijo de una cuenta que la purga acaba de borrar las viola.</summary>
    internal static readonly string[] AccountForeignKeys =
    [
        "FK_email_tokens_accounts_account_id",
        "FK_profiles_accounts_account_id",
        "FK_sessions_accounts_account_id",
    ];

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException. La clave foránea va con ella: es la misma carrera
        // (la cuenta desapareció desde que se leyó) vista desde el INSERT de un hijo en vez del UPDATE de la cuenta, y cuál de
        // las dos sentencias del lote falla primero depende del orden en que EF las manda.
        catch (DbUpdateException exception)
            when (exception is DbUpdateConcurrencyException || ForeignKeyViolation.Is(exception, AccountForeignKeys))
        {
            // La cuenta cambió (xmin) o desapareció (la purga) desde que se leyó. Mismo motivo que abajo para limpiar el tracker:
            // quien llama descarta y reintenta con lo que hay ahora en la base.
            dbContext.ChangeTracker.Clear();
            throw new RequestConcurrencyException(
                ConcurrencyConflictCode,
                "The buyer account changed while the operation was being committed.",
                exception);
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

    public async Task SaveChangesAsync(Func<CancellationToken, Task> afterSave, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(afterSave);

        // Si SaveChanges o afterSave fallan, el dispose sin Commit revierte los dos.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await SaveChangesAsync(cancellationToken);
        await afterSave(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();
}
