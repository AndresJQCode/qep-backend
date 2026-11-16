using Microsoft.EntityFrameworkCore;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Los adjuntos del formulario público de Soporte sobre la subida sin miembro de Storage (plan F30 a F32). Support no ve a Storage
/// (<c>SupportLayerTests</c>), así que el dueño <see cref="FileOwnerType.SupportTicket"/> y el tope de 10 MB se ponen acá. El
/// <c>uploadKey</c> es el <c>OwnerId</c> del archivo: Storage exige el par en cada operación y uno que no casa responde igual que un id
/// que no existe. Nada de esto pasa por <c>StorageAuthorization</c>: la consola sigue igual.
/// <para><see cref="CountPendingAsync"/> (owner, 2026-11-15, E2) es SQL crudo sobre la conexión de <see cref="SupportDbContext"/> y lee
/// por nombre <c>storage.file_resources</c> (<c>tenant_id</c>, <c>owner_type</c>, <c>created_at</c>, <c>deleted_at</c>, <c>id</c>) y
/// <c>support.ticket_attachments</c> (<c>file_id</c>, por <c>IX_ticket_attachments_file</c>) y <c>owner_id</c>. Las notas usan el mismo
/// dueño <see cref="FileOwnerType.SupportTicket"/> con la clave <c>SupportNoteUploads.OwnerKey</c> (plan N18), que es un UUID v8; la
/// del formulario es v4 (<c>SupportPublicUploads.KeyVersion</c>). El tope cuenta sólo las v4 —la versión es el nibble alto del byte 6
/// del UUID, <c>get_byte(uuid_send(owner_id), 6) &gt;&gt; 4</c>, que corre en cualquier Postgres; <c>uuid_extract_version</c> exige 17+ y la
/// versión de producción no está probada en el repo (revisión final S1)—: una subida de nota, reclamada o no, nunca cuenta, y un miembro que abre subidas sin adjuntarlas no deja al formulario en <c>attachments_busy</c>
/// (revisión del Bloque B). Renombrar una columna compila y deja
/// <c>POST …/attachments</c> en 500; lo amarra <c>SupportPublicAttachmentsApiTests</c>, que corre contra el esquema migrado.</para>
/// </summary>
internal sealed class SupportAttachmentStorage(IStorageSystemUploads uploads, SupportDbContext support) : ISupportAttachmentStorage
{
    private static readonly string SupportTicketOwner = nameof(FileOwnerType.SupportTicket);

    // La hora es la de la base, la misma que usa la prueba al sembrar; Storage escribe created_at con el reloj de la API, que en
    // producción es el mismo con diferencia de milisegundos.
    public async Task<int> CountPendingAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken) =>
        await support.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value"
            FROM storage.file_resources f
            WHERE f.tenant_id = {tenantId}
              AND f.owner_type = {SupportTicketOwner}
              AND f.created_at >= now() - {window}
              AND f.deleted_at IS NULL
              AND (get_byte(uuid_send(f.owner_id), 6) >> 4) = {SupportPublicUploads.KeyVersion}
              AND NOT EXISTS (SELECT 1 FROM support.ticket_attachments a WHERE a.file_id = f.id)
            """).SingleAsync(cancellationToken);

    public async Task<SupportAttachmentUpload> OpenAsync(
        Guid tenantId, Guid uploadKey, string name, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        try
        {
            var session = await uploads.OpenAsync(
                tenantId,
                FileOwnerType.SupportTicket,
                uploadKey,
                name,
                mimeType,
                sizeBytes,
                SupportPublicAttachmentRules.MaxSizeBytes,
                cancellationToken);
            return new SupportAttachmentUpload(session.FileId, session.UploadUrl, session.Headers);
        }
        catch (StorageDomainException exception)
        {
            // El validador de Soporte ya filtró nombre, tipo y tamaño con las mismas reglas (SupportAttachmentParityTests): llegar acá
            // es una declaración que Storage ve distinto. La página recibe el código de Soporte, no el de Storage.
            throw new SupportDomainException(SupportErrorCodes.PublicAttachmentInvalid, $"The attachment was rejected ({exception.Code}).");
        }
    }

    public async Task<SupportAttachmentCompletion> CompleteAsync(
        Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken) =>
        (await uploads.CompleteAsync(tenantId, fileId, FileOwnerType.SupportTicket, uploadKey, cancellationToken)).Outcome switch
        {
            SystemUploadOutcome.Available => SupportAttachmentCompletion.Available,
            SystemUploadOutcome.Rejected => SupportAttachmentCompletion.Rejected,
            SystemUploadOutcome.ObjectMissing => SupportAttachmentCompletion.NotUploaded,
            _ => SupportAttachmentCompletion.NotFound,
        };

    public async Task<SupportAttachmentFile?> FindAvailableAsync(
        Guid tenantId, Guid fileId, Guid uploadKey, CancellationToken cancellationToken) =>
        await uploads.FindAvailableAsync(tenantId, fileId, FileOwnerType.SupportTicket, uploadKey, cancellationToken) is { } file
            ? new SupportAttachmentFile(file.FileId, file.Name, file.MimeType, file.SizeBytes)
            : null;

    public Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        uploads.CreateDownloadUrlAsync(tenantId, fileId, cancellationToken);
}
