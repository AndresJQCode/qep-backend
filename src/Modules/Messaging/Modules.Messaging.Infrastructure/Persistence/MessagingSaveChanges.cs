using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Traducción de errores de guardado del módulo, compartida por <see cref="MessagingUnitOfWork"/> y por los repositorios que abren su
/// propia transacción (<c>BusinessHoursRepository.SwapDefaultAsync</c>), para no duplicar los catch.
/// La versión vieja es <c>concurrency.conflict</c> (412 por If-Match en resolve/reopen, §8.5). Spec 2026-10-12 §3.4: el atajo
/// repetido es <c>messaging.quick_reply.shortcut_taken</c> (422). Spec 2026-10-14 §4.1: nombre e idioma repetidos de una plantilla son
/// <c>messaging.template.name_taken</c> (422), una segunda <c>Quotation</c> en la conexión es <c>messaging.template.quotation_exists</c>
/// (422) y un <c>meta_template_id</c> o un marcador de sincronización repetidos son <c>concurrency.conflict</c> (dos refrescos a la vez).
/// Spec 2026-10-15: el nombre repetido de un motivo de resolución es <c>messaging.resolution_reason.name_taken</c> (409).</summary>
internal static class MessagingSaveChanges
{
    public static async Task<int> SaveAsync(MessagingDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella (mismo orden que CatalogUnitOfWork).
        catch (DbUpdateConcurrencyException exception)
        {
            // Los llenados implícitos del espejo atrapan cualquier RequestConcurrencyException: sin soltar lo pendiente del espejo, el
            // siguiente SaveChanges del request lo reinsertaría.
            DiscardMirrorChanges(dbContext);
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
        // Revisión de la rama, hallazgo 3: dos delegaciones vivas de una conversación. La de dominio y no un 23505 crudo: el motor de chatbots
        // la atiende como cualquier cambio que la conversación ya no admite (la corrida termina Interrupted) en vez de reintentar la fila.
        catch (DbUpdateException exception) when (IsLiveDelegationConflict(exception))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.DelegationLiveExists, "The conversation already has a live delegation to an agent.", exception);
        }
        // Spec 2026-10-15 §3.3 y D-R11: 409 y no 422, como customers.tag.name_taken. Es un choque con lo que ya existe —el resolver
        // reintenta y lo reutiliza; el renombrar elige otro nombre—, por eso ResourceConflictException y no un error de dominio.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.ResolutionReasonNameIndex, StringComparison.Ordinal))
        {
            throw new ResourceConflictException(
                MessagingErrorCodes.ResolutionReasonNameTaken, "Another active resolution reason in this tenant already uses that name.", exception);
        }
        // Spec 2026-10-21 §5.1: 409 y no 422, como el motivo de resolución. El nombre repetido (sin distinguir mayúsculas) y la carrera entre
        // dos «predeterminado» son índices distintos: se discrimina por nombre para no mandar a corregir el campo equivocado.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.BusinessHoursNameIndex, StringComparison.Ordinal))
        {
            throw new ResourceConflictException(
                MessagingErrorCodes.BusinessHoursNameTaken, "Another business hours schedule in this tenant already uses that name.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.BusinessHoursDefaultIndex, StringComparison.Ordinal))
        {
            throw new ResourceConflictException(
                MessagingErrorCodes.BusinessHoursDefaultConflict, "This tenant already has another default business hours schedule.", exception);
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
        // Spec 2026-11-17 (fix round 2 del bloque C): una imagen de encabezado por plantilla. La creación que pierde la carrera es
        // header_image_rejected sobre el campo, como el rechazo de antes de publicar.
        catch (DbUpdateException exception) when (IsHeaderImageTaken(exception))
        {
            throw HeaderImageTaken(exception);
        }
        // Fix de la Task 8: dos primeros aprovisionamientos de la plantilla de cotización a la vez. El que pierde es quotation_exists (422):
        // la versión que alcanzó a crear en Meta queda allá y el siguiente refresco la trae como External.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateQuotationIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateQuotationExists, "This connection already has its quotation template.", exception);
        }
        // Spec 2026-11-09 D1: lo mismo para la plantilla de pedido.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateOrderIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateOrderExists, "This connection already has its order template.", exception);
        }
        // Spec 2026-11-21: lo mismo para la plantilla de factura.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateInvoiceIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateInvoiceExists, "This connection already has its invoice template.", exception);
        }
        // Soporte fase 2: lo mismo para la plantilla «Aviso de respuesta».
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateSupportReplyIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportReplyExists, "This connection already has its support reply template.", exception);
        }
        // Soporte fase 4b: lo mismo para la «Respuesta formal» y la encuesta.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateSupportFormalReplyIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportFormalReplyExists, "This connection already has its support formal reply template.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateSupportSurveyIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportSurveyExists, "This connection already has its support survey template.", exception);
        }
        // Mayorista: lo mismo para la plantilla del aviso de invitación de distribuidor.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateWholesaleInvitationNoticeIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateWholesaleInvitationNoticeExists, "This connection already has its wholesale invitation notice template.",
                exception);
        }
        // Support task notices: the same for the assignment notice template.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateTaskNoticeIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateTaskNoticeExists, "This connection already has its task notice template.", exception);
        }

        // HR birthday notices: the same for the birthday notice template.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateHrBirthdayNoticeIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHrBirthdayNoticeExists, "This connection already has its birthday notice template.", exception);
        }

        // Agenda: lo mismo para la plantilla del recordatorio de cita.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateAppointmentReminderIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateAppointmentReminderExists, "This connection already has its appointment reminder template.",
                exception);
        }
        // Spec 2026-11-09: un programado activo por conversación y un clientId por conversación. Los dos son 409 ya_existe: el handler de
        // crear relee por clientId y devuelve el existente si fue el mismo reintento.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  (string.Equals(postgres.ConstraintName, MessagingDbContext.ScheduledActiveConversationIndex, StringComparison.Ordinal) ||
                   string.Equals(postgres.ConstraintName, MessagingDbContext.ScheduledConversationClientIndex, StringComparison.Ordinal)))
        {
            throw new ResourceConflictException(
                MessagingErrorCodes.ScheduledAlreadyExists, "This conversation already has a scheduled message.", exception);
        }
        // Cobros recurrentes §5: lo mismo para las plantillas de cobro.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateBillingIndex, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateBillingExists, "This connection already has its billing templates.", exception);
        }
        // Dos refrescos de la misma conexión a la vez: el segundo pierde y el cliente reintenta (412, como la versión vieja). Lo mismo con
        // el marcador de template_mirror_syncs: dos primeros llenados lo insertan los dos y el segundo choca con su PK.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  (string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateMetaIdIndex, StringComparison.Ordinal) ||
                   string.Equals(postgres.ConstraintName, MessagingDbContext.TemplateMirrorSyncPrimaryKey, StringComparison.Ordinal)))
        {
            DiscardMirrorChanges(dbContext);
            throw new RequestConcurrencyException("concurrency.conflict", "Another sync of the same connection committed first.", exception);
        }
        // Spec 2026-10-21 D-H10: dos PUT que reclaman la misma conexión sin asignar insertan los dos su fila y el segundo choca con la PK
        // (una conexión tiene a lo sumo un horario). Pierde el segundo y el cliente reintenta, como con la sincronización de plantillas.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, MessagingDbContext.ConnectionBusinessHoursPrimaryKey, StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "Another request assigned the connection to a schedule first.", exception);
        }
    }

    /// <summary>Por nombre de índice, como el resto: otro 23505 de <c>message_templates</c> no es una imagen tomada. Spec 2026-11-20: el
    /// índice único de las imágenes retiradas cuenta igual: dos cambios a la vez con el mismo archivo, o un archivo ya retirado, son el
    /// mismo rechazo del campo y no un 500.</summary>
    internal static bool IsHeaderImageTaken(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && (string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateHeaderImageIndex, StringComparison.Ordinal)
            || string.Equals(postgres.ConstraintName, MessagingDbContext.MessageTemplateRetiredHeaderImageFileIndex, StringComparison.Ordinal));

    /// <summary>El error del campo de la imagen (el mismo que el rechazo de antes de publicar), con la violación como causa.</summary>
    internal static MessagingDomainException HeaderImageTaken(DbUpdateException exception) =>
        new(
            MessagingErrorCodes.TemplateHeaderImageRejected,
            "The header image already belongs to another template.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["headerImageFileId"] = [TemplateMessages.HeaderImage] },
            exception);

    /// <summary>Hallazgo 3: por nombre de índice, como el resto; un 23505 de otro índice de la tabla no es este choque.</summary>
    internal static bool IsLiveDelegationConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, MessagingDbContext.BotDelegationLiveIndex, StringComparison.Ordinal);

    /// <summary>
    /// La sincronización que perdió deja sus filas del espejo pendientes en el contexto, y el catálogo atrapa el 412 del llenado implícito
    /// y sigue (<c>MirroredTemplateCatalog</c>). Sin soltarlas, el siguiente <c>SaveChanges</c> del mismo request (el de la campaña que
    /// se estaba creando) las volvería a insertar y chocaría otra vez. Sólo se sueltan las del espejo: lo demás pendiente es del handler.
    /// <para>Revisión final (spec 2026-11-17): también los avisos de liberación de la imagen de encabezado que la sincronización agregó al
    /// eliminar filas. Van con la eliminación: si ésta se suelta y el aviso no, el siguiente guardado liberaría la imagen de una fila que
    /// sigue existiendo.</para>
    /// </summary>
    internal static void DiscardMirrorChanges(MessagingDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is MessageTemplate or TemplateMirrorSyncRecord
                         || (entry.State == EntityState.Added && entry.Entity is MessagingOutboxRecord
                         {
                             EventName: MessagingIntegrationEvents.TemplateHeaderImageReleased,
                         }))
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
