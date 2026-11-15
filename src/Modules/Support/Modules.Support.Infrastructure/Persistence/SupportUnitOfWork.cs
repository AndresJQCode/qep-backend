using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportUnitOfWork(SupportDbContext dbContext) : ISupportUnitOfWork
{
    // Borrar clientes (spec 2026-11-10, P1/P2): guardar un ticket con cliente es escribir una referencia a customers.customers, que
    // no tiene FK. CustomerReferenceWrites toma el candado compartido de cada cliente y, ya con él, comprueba que la fila exista,
    // todo dentro de la transacción que guarda: si el borrado ganó la carrera, el candado espera a su commit y la comprobación ve
    // el cliente ya borrado (Review Focus 5).
    private static readonly CustomerReferenceSource TicketCustomer =
        new(typeof(SupportTicket), nameof(SupportTicket.CustomerId), SupportErrorCodes.CustomerNotFound);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CustomerReferenceWrites.SaveHoldingAsync(
                dbContext,
                CustomerReferenceWrites.Collect(dbContext.ChangeTracker, TicketCustomer),
                CustomerNotFound,
                cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The support record changed while the update was being committed.",
                exception);
        }
        // Por nombre de índice y no por 23505 (precedente TenancyUnitOfWork): cada índice único es una regla distinta.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketTypeNameIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.TicketTypeNameTaken,
                "A ticket type with this name already exists.",
                exception);
        }
        // Dos guardados del mismo ticket que agregan el mismo ordinal: pierde el segundo y sólo tiene que recargar, igual que con
        // la versión.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketEventOrdinalIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The ticket trace changed while the update was being committed.",
                exception);
        }
        // Los tres únicos de la fase 2 (plan E3 y E16): dos réplicas del seguimiento sobre el mismo entrante, o un reintento con el
        // mismo clientId. Quien guarda los atrapa por código: el que perdió no tiene nada que hacer.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketMessageIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.MessageAlreadyLinked,
                "The message is already linked to this ticket.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.UnclassifiedMessageKey))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.UnclassifiedAlreadyRecorded,
                "The message is already recorded as unclassified.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketReplyClientIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.ReplyDuplicateClientId,
                "A reply with this client id already exists for the ticket.",
                exception);
        }
        // Fase 3 (plan F14 y F16): dos guardados que crean la misma versión del formulario o de los textos legales. El If-Match ya
        // filtró la mayoría; esto es la carrera entre dos que leyeron la misma versión vigente.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.FormVersionIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.FormVersionTaken,
                "This version of the public form already exists.",
                exception);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.LegalTextVersionIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.LegalTextsVersionTaken,
                "This version of the legal texts already exists.",
                exception);
        }
        // Triage #2 del Bloque F: una fila de consentimiento por ticket. Inalcanzable hoy; traducido igual para que un camino nuevo que
        // lo duplique reciba un 409 con código y no un 500.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.ConsentTicketIndex))
        {
            throw new ResourceConflictException(
                SupportErrorCodes.PublicConsentDuplicate,
                "The ticket already has its consent.",
                exception);
        }
        // Plan F35: otra radicación ya reclamó el archivo. El 422 de siempre de un adjunto inválido, no un 409: la página no distingue
        // un archivo ajeno de uno que no existe, y la transacción del radicado se deshace entera.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketAttachmentFileIndex))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicAttachmentInvalid, "The attachment is not valid.");
        }
        // Spec 2026-11-15 §5.4 (plan N15): two notes with the same clientId at once. The loser cannot tell whether the winner was its
        // own retry, so it answers the conflict and the screen rereads.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketNoteKey))
        {
            throw new ResourceConflictException(
                "concurrency.conflict",
                "A note with this client id was created at the same time.",
                exception);
        }
        // Another note claimed the same file first: the same 422 as any file that is not valid for this note.
        catch (DbUpdateException exception) when (UniqueViolation.Is(exception, SupportDbContext.TicketNoteFileIndex))
        {
            throw new SupportDomainException(SupportErrorCodes.NoteFileInvalid, "The note file is not valid.");
        }
    }

    // Copia de CustomersUnitOfWork.BeginTransactionAsync. Los SaveChanges que corran dentro siguen pasando por la traducción de
    // arriba, y el Dispose sin Commit deshace todo, también el número que emitió SupportTicketNumberGenerator.
    public async Task<ISupportTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new SupportTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    // El mismo protocolo que SaveHoldingAsync (candado y después existencia, en READ COMMITTED), adelantado: sin transacción
    // abierta, CustomerReferenceWrites lanza. Pedir otra vez el candado compartido en la misma transacción no espera.
    public async Task HoldCustomerReferenceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        if (!await CustomerReferenceWrites.HoldAsync(dbContext.Database, tenantId, customerId, cancellationToken))
        {
            throw CustomerNotFound(new CustomerReferenceToHold(tenantId, customerId, TicketCustomer.NotFoundCode));
        }
    }

    public void DiscardChanges() => dbContext.ChangeTracker.Clear();

    private static SupportDomainException CustomerNotFound(CustomerReferenceToHold reference) =>
        new(reference.NotFoundCode, $"Customer '{reference.CustomerId}' was not found.");

    private sealed class SupportTransaction(IDbContextTransaction transaction) : ISupportTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
