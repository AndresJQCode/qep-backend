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

    // Sin transacción (o fuera de READ COMMITTED) CustomerReferenceWrites lanza: el candado no protegería nada.
    internal static Task<bool> HoldAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        CustomerReferenceWrites.HoldAsync(dbContext.Database, tenantId, customerId, cancellationToken);

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
    /// Para el webhook: el cliente de una conversación, leído sin candado. Si la fila ya no está (el borrado se la llevó entre la búsqueda
    /// y esta lectura), <see cref="CustomerVanishedException"/>: lo mismo que si el cliente hubiera desaparecido.
    /// </summary>
    internal static async Task<Guid?> OwnerOfAsync(MessagingDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<Guid?>(
            $"""SELECT customer_id AS "Value" FROM messaging.conversations WHERE id = {conversationId}""")
            .ToListAsync(cancellationToken);
        return rows is [var owner]
            ? owner
            : throw new CustomerVanishedException($"Conversation '{conversationId}' was deleted while a webhook delivery was writing to it.");
    }

    /// <summary>
    /// Orden de candados frente al borrado (P1 y P5): el borrado toma los exclusivos ordenados por id y después bloquea con
    /// <c>FOR UPDATE</c> las conversaciones de esos clientes. El webhook, en cambio, toma un solo candado de cliente y lo toma antes de
    /// cualquier fila, así que nunca espera un candado teniendo algo que el borrado necesite. Ese único candado es el del cliente que la
    /// conversación queda teniendo: el que ya tenía (el <c>COALESCE</c> lo conserva) o, si no tenía, el resuelto afuera. Si después de
    /// tomarlo la conversación resulta colgar de otro (otro request se la cambió o la creó entretanto), no se escribe sin su candado: se
    /// reintenta la entrega.
    /// </summary>
    internal static void EnsureAttachedIsHeld(Guid? attached, Guid? held)
    {
        if (attached is { } customerId && customerId != held)
        {
            throw new CustomerVanishedException(
                $"The conversation moved to customer '{customerId}' while a webhook delivery held the lock of another one.");
        }
    }
}

/// <summary>
/// El cliente al que el webhook iba a colgar un mensaje se borró mientras esperaba el candado (A-12). No es un fallo: la entrega se
/// reintenta, y el reintento ya no encuentra al cliente ni su conversación y crea los nuevos (D-4).
/// </summary>
internal sealed class CustomerVanishedException(string message) : Exception(message);
