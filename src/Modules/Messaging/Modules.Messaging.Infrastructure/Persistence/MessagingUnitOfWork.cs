using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>La versión vieja es <c>concurrency.conflict</c> (412 por If-Match en resolve/reopen, §8.5). Spec 2026-10-12 §3.4: el atajo
/// repetido es <c>messaging.quick_reply.shortcut_taken</c> (422). Spec 2026-10-14 §4.1: nombre e idioma repetidos de una plantilla son
/// <c>messaging.template.name_taken</c> (422) y un <c>meta_template_id</c> o un marcador de sincronización repetidos son
/// <c>concurrency.conflict</c> (dos refrescos a la vez).</summary>
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
        // Spec 2026-10-14 §4.1: nombre e idioma repetidos en la misma conexión son name_taken (422, sin mapa errors, como shortcut_taken).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateNameLanguageIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateNameTaken, "Another template of this connection already uses that name in that language.", exception);
        }
        // Dos refrescos de la misma conexión a la vez: el segundo pierde y el cliente reintenta (412, como la versión vieja). Lo mismo con
        // el marcador de template_mirror_syncs: dos primeros llenados lo insertan los dos y el segundo choca con su PK.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  (string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateMetaIdIndex, StringComparison.Ordinal) ||
                   string.Equals(postgres.ConstraintName, MessagingDbContext.TemplateMirrorSyncPrimaryKey, StringComparison.Ordinal)))
        {
            DiscardMirrorChanges();
            throw new RequestConcurrencyException("concurrency.conflict", "Another sync of the same connection committed first.", exception);
        }
    }

    /// <summary>
    /// La sincronización que perdió deja sus filas del espejo pendientes en el contexto, y el catálogo atrapa el 412 del llenado implícito
    /// y sigue (<c>MirroredTemplateCatalog</c>). Sin soltarlas, el siguiente <c>SaveChanges</c> del mismo request (el de la campaña que
    /// se estaba creando) las volvería a insertar y chocaría otra vez. Sólo se sueltan las del espejo: lo demás pendiente es del handler.
    /// </summary>
    private void DiscardMirrorChanges()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is MessageTemplate or TemplateMirrorSyncRecord)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
