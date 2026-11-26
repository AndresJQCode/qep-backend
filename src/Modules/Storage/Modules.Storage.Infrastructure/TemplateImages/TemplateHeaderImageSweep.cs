using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.Infrastructure.TemplateImages;

// Spec 2026-11-17, «Closing the residual gaps»: la red final para los caminos que hoy sólo registran (la revisión de uso que falla, el
// aviso del outbox que no se escribe, una subida cuya creación nunca llegó). Suelta las imágenes de encabezado de más de 24 h que ninguna
// fila de plantilla usa; el recorrido es el de UnreferencedFileSweep. La ventana que deja la revisión bajo el candado (la creación guarda
// después de esa pregunta) la cierra Messaging, que no acepta una imagen subida hace más de 12 h.
internal sealed class TemplateHeaderImageSweep(
    IUnreferencedFileCandidates files,
    ITemplateImageReferences references,
    UnreferencedFileReleaser releaser,
    ILogger<TemplateHeaderImageSweep> logger) : UnreferencedFileSweep(files, releaser, logger)
{
    internal const string UnreferencedReason = "template_header_image_unreferenced";

    public override FileOwnerType OwnerType => FileOwnerType.MessageTemplate;

    protected override string Reason => UnreferencedReason;

    protected override Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
        references.FindReferencedAsync(tenantId, fileIds, cancellationToken);
}
