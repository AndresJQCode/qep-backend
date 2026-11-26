using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.UnreferencedFiles;

// Spec 2026-11-18 («Lifecycle»), plan Task 11: la red final de los archivos de un bloque «Mensaje». Suelta el que ningún borrador, versión,
// plantilla guardada ni mensaje de la bandeja nombra desde hace más de 24 h: una subida que nunca se guardó, un archivo que se cambió por
// otro, o el de un chatbot borrado cuyo aviso no se procesó. La pregunta la responde Bootstrapper (Automations y Messaging).
internal sealed class ChatbotMediaSweep(
    IUnreferencedFileCandidates files,
    IChatbotMediaReferences references,
    UnreferencedFileReleaser releaser,
    ILogger<ChatbotMediaSweep> logger) : UnreferencedFileSweep(files, releaser, logger)
{
    internal const string UnreferencedReason = "chatbot_media_unreferenced";

    public override FileOwnerType OwnerType => FileOwnerType.ChatbotMedia;

    protected override string Reason => UnreferencedReason;

    protected override Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
        references.FindReferencedAsync(tenantId, fileIds, cancellationToken);
}
