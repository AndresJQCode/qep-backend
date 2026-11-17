using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Unidad de trabajo del módulo: delega en <see cref="MessagingSaveChanges"/>, que lleva la traducción de errores de la base.</summary>
internal sealed class MessagingUnitOfWork(MessagingDbContext dbContext) : IMessagingUnitOfWork
{
    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);

    public async Task<IMessagingTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new MessagingTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private sealed class MessagingTransaction(IDbContextTransaction transaction) : IMessagingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback suelta los candados de transacción que se tomaron en ella.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
