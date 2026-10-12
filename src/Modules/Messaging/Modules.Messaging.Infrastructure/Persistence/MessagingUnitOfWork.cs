using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La versión vieja es <c>concurrency.conflict</c> (412 por If-Match en resolve/reopen, §8.5). Spec 2026-10-12 §3.4: el atajo
/// repetido es <c>messaging.quick_reply.shortcut_taken</c> (422).</summary>
internal sealed class MessagingUnitOfWork(MessagingDbContext dbContext) : IMessagingUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella (mismo orden que CatalogUnitOfWork).
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The conversation changed while the operation was being committed.", exception);
        }
        // Por nombre de índice y no sólo por 23505: el esquema tiene otros únicos (IX_messages_conversation_client, …) y responder
        // shortcut_taken por uno de ellos mandaría a corregir el campo equivocado (precedente: catalog.product.code_taken, SDD-CT-06).
        // Traducido acá y no en Application, que no referencia Npgsql (MessagingLayerTests).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.QuickReplyShortcutIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyShortcutTaken, "Another quick reply in this tenant already uses that shortcut.", exception);
        }
    }
}
