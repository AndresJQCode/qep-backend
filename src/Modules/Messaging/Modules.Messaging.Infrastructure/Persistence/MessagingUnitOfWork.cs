using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Unidad de trabajo del módulo: delega en <see cref="MessagingSaveChanges"/>, que lleva la traducción de errores de la base.</summary>
internal sealed class MessagingUnitOfWork(MessagingDbContext dbContext) : IMessagingUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
}
