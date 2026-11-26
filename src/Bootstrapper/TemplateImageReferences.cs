using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17, «Closing the residual gaps»: qué imágenes de encabezado usa todavía una fila de
/// <c>messaging.message_templates</c> (<c>header_image_file_id</c>), para el barrido diario de Storage. LINQ sobre
/// <see cref="MessagingDbContext"/> y no SQL crudo: renombrar la propiedad o la columna rompe el build o el mapeo, no el barrido en
/// silencio. No reusa <c>IMessageTemplateRepository.IsHeaderImageInUseAsync</c> porque pregunta de a un archivo y el barrido pregunta
/// por una página de hasta 200. Las plantillas se borran de verdad (no hay borrado lógico), así que toda fila que lo nombra lo usa,
/// también una plantilla rechazada. El tenant va en la consulta: el id que use otro tenant no cuenta.
/// </summary>
internal sealed class TemplateImageReferences(MessagingDbContext messaging) : ITemplateImageReferences
{
    public async Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = fileIds.ToList();
        var referenced = await messaging.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId
                && template.HeaderImageFileId != null
                && ids.Contains(template.HeaderImageFileId.Value))
            .Select(template => template.HeaderImageFileId!.Value)
            .ToListAsync(cancellationToken);
        // Spec 2026-11-20: las imágenes que una plantilla retiró al cambiarlas siguen referenciadas hasta que se borre la plantilla.
        var retired = await messaging.MessageTemplateRetiredHeaderImages.AsNoTracking()
            .Where(image => image.TenantId == tenantId && ids.Contains(image.FileId))
            .Select(image => image.FileId)
            .ToListAsync(cancellationToken);
        return referenced.Concat(retired).ToHashSet();
    }
}
