using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// El candado de <see cref="CustomerReferenceLock"/> sobre la conexión de <see cref="MessagingDbContext"/> (A-12 y P4). Primero el
/// candado y después la lectura (P1), en sentencias aparte y en READ COMMITTED: al revés se vería un cliente a punto de
/// desaparecer. Las ingestas del webhook son estáticas y reciben el contexto, así que usan las variantes estáticas de aquí.
/// </summary>
internal sealed class MessagingCustomerReferenceLock(MessagingDbContext dbContext) : IMessagingCustomerReferenceLock
{
    public Task<bool> HoldAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        HoldAsync(dbContext, tenantId, customerId, cancellationToken);

    internal static async Task<bool> HoldAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        // Un candado de transacción sin transacción se suelta al terminar su propia sentencia y no protege nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The customer reference lock needs an open Messaging transaction.");
        }

        await dbContext.Database.ExecuteSqlAsync(CustomerReferenceLock.Shared(tenantId, customerId), cancellationToken);
        return await dbContext.Database.SqlQuery<bool>(CustomerReferenceLock.Exists(tenantId, customerId)).SingleAsync(cancellationToken);
    }

    /// <summary>Para el webhook: si el cliente ya no existe, <see cref="CustomerVanishedException"/>, que deshace la transacción de la
    /// ingesta y el procesador traduce a reintento.</summary>
    internal static async Task HoldOrVanishAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        if (!await HoldAsync(dbContext, tenantId, customerId, cancellationToken))
        {
            throw new CustomerVanishedException($"Customer '{customerId}' was deleted while a webhook delivery was attaching a conversation to it.");
        }
    }

    /// <summary>
    /// Para el webhook: el cliente que la conversación ya tiene, si es otro que <paramref name="held"/>. El <c>COALESCE(customer_id, …)</c>
    /// de la ingesta se queda con ése, y el mensaje cuelga de su conversación: si se está borrando, hay que esperarlo igual.
    /// </summary>
    internal static async Task HoldConversationCustomerAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid conversationId, Guid? held, CancellationToken cancellationToken)
    {
        var owner = await dbContext.Database.SqlQuery<Guid?>(
            $"""SELECT customer_id AS "Value" FROM messaging.conversations WHERE id = {conversationId}""")
            .SingleAsync(cancellationToken);
        if (owner is { } customerId && customerId != held)
        {
            await HoldOrVanishAsync(dbContext, tenantId, customerId, cancellationToken);
        }
    }
}

/// <summary>
/// El cliente al que el webhook iba a colgar un mensaje se borró mientras esperaba el candado (A-12). No es un fallo: la entrega se
/// reintenta, y el reintento ya no encuentra al cliente ni su conversación y crea los nuevos (D-4).
/// </summary>
internal sealed class CustomerVanishedException(string message) : Exception(message);
