using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-11 decisión 9 y §5.2: abre (o encuentra) la conversación de un cliente en una conexión, en SQL como la ingesta
/// (<c>InboundIngestion.ResolveConversationAsync</c>) y respetando sus mismos índices únicos. Busca por BSUID, por el teléfono de una conversación
/// con BSUID, por <c>customer_id</c> y, al final, por el teléfono de una fila vieja sin BSUID (orden en <c>FindAsync</c>); si no existe inserta con <c>ON CONFLICT DO NOTHING</c> y, si perdió
/// una carrera, vuelve a buscar. Una conversación creada sólo con teléfono (<c>user_id NULL</c>) la adopta la ingesta cuando llega el
/// BSUID (paso 1a del spec 2026-10-10 §8.1): no hay duplicado.
/// </summary>
internal sealed class ConversationOpener(MessagingDbContext dbContext, IMessagingAuditRecorder audit) : IConversationOpener
{
    public async Task<OpenedConversation> OpenAsync(
        Guid tenantId, Guid connectionId, MessagingCustomerContact contact, Guid? assignedMemberId, Guid? actorUserId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var userId = contact.WhatsAppUserId;
        var waId = DigitsOf(contact.PhoneE164);
        if (userId is null && waId is null)
        {
            throw new InvalidOperationException("A contact needs a phone number or a WhatsApp user id to open a conversation.");
        }

        if (await FindAsync(tenantId, connectionId, contact.CustomerId, userId, waId, cancellationToken) is { } existing)
        {
            return new OpenedConversation(existing, false);
        }

        var at = now.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var id = Guid.CreateVersion7();
        // El blanco del ON CONFLICT es el índice que protege la clave con la que se inserta: el BSUID, o el teléfono de una fila sin él.
        var inserted = userId is not null
            ? await dbContext.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, profile_name, customer_id, assigned_member_id, assigned_at,
                                                     status, unread_count, last_activity_at, created_at, updated_at, version)
                VALUES ({id}, {tenantId}, {connectionId}, {userId}, {waId}, {contact.Name}, {contact.CustomerId}, {assignedMemberId},
                        CASE WHEN {assignedMemberId}::uuid IS NULL THEN NULL ELSE {at} END, 'Open', 0, {at}, {at}, {at}, 1)
                ON CONFLICT (connection_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken)
            : await dbContext.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, profile_name, customer_id, assigned_member_id, assigned_at,
                                                     status, unread_count, last_activity_at, created_at, updated_at, version)
                VALUES ({id}, {tenantId}, {connectionId}, NULL, {waId}, {contact.Name}, {contact.CustomerId}, {assignedMemberId},
                        CASE WHEN {assignedMemberId}::uuid IS NULL THEN NULL ELSE {at} END, 'Open', 0, {at}, {at}, {at}, 1)
                ON CONFLICT (connection_id, wa_id) WHERE user_id IS NULL DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
        if (inserted.Count == 0)
        {
            // Otro request o la ingesta la creó entre el SELECT y el INSERT. El SELECT va en una sentencia aparte: así ve lo commiteado.
            await transaction.RollbackAsync(cancellationToken);
            var raced = await FindAsync(tenantId, connectionId, contact.CustomerId, userId, waId, cancellationToken)
                ?? throw new InvalidOperationException("The conversation that blocked the insert could not be found.");
            return new OpenedConversation(raced, false);
        }

        if (assignedMemberId is { } member)
        {
            // Como `take`: el evento queda en el hilo y la acción en la auditoría, en la misma transacción que la fila.
            await Webhook.ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, id,
                new ConversationEvent(ConversationEventType.Taken, Actor: member), at, at, cancellationToken);
            if (actorUserId is { } actor)
            {
                audit.Record(tenantId, actor, MessagingAuditActions.Started, id, at);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new OpenedConversation(id, true);
    }

    /// <summary>
    /// Orden (spec 2026-10-24 §4.3): (1) por BSUID del cliente; (2) la conversación con BSUID de la conexión que tiene ese <c>wa_id</c>, la
    /// de actividad más reciente: es la que recibe las respuestas, y va antes que (3) porque una huérfana de <c>customer_id</c> del defecto
    /// anterior no puede ganarle; (3) por <c>customer_id</c>, la más reciente; (4) la fila vieja sin BSUID con ese teléfono. El
    /// <c>customer_id</c> de la encontrada no cambia (cambiarlo es <c>PUT …/customer</c>) y las huérfanas existentes no se tocan.
    /// </summary>
    private async Task<Guid?> FindAsync(
        Guid tenantId, Guid connectionId, Guid customerId, string? userId, string? waId, CancellationToken cancellationToken)
    {
        if (userId is not null)
        {
            var byUser = await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND user_id = {userId}""")
                .ToListAsync(cancellationToken);
            if (byUser.Count == 1)
            {
                return byUser[0];
            }
        }

        if (waId is not null)
        {
            // La conversación con BSUID de ese número es la que recibe las respuestas: gana a una huérfana de `customer_id` (CD-10).
            var byPhone = await dbContext.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM messaging.conversations
                WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND user_id IS NOT NULL AND wa_id = {waId}
                ORDER BY last_activity_at DESC, id DESC LIMIT 1
                """).ToListAsync(cancellationToken);
            if (byPhone.Count == 1)
            {
                return byPhone[0];
            }
        }

        var byCustomer = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT id AS "Value" FROM messaging.conversations
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND customer_id = {customerId}
            ORDER BY last_activity_at DESC, id DESC LIMIT 1
            """).ToListAsync(cancellationToken);
        if (byCustomer.Count == 1)
        {
            return byCustomer[0];
        }

        if (waId is not null)
        {
            var legacy = await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM messaging.conversations WHERE tenant_id = {tenantId} AND connection_id = {connectionId} AND user_id IS NULL AND wa_id = {waId}""")
                .ToListAsync(cancellationToken);
            if (legacy.Count == 1)
            {
                return legacy[0];
            }
        }

        return null;
    }

    /// <summary>Los dígitos del E.164 (el <c>wa_id</c> de Meta no lleva «+»); <c>null</c> si no hay ninguno.</summary>
    private static string? DigitsOf(string? phoneE164)
    {
        var digits = phoneE164 is null ? string.Empty : new string(phoneE164.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }
}
