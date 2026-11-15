using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record CompletePublicAttachmentCommand(string? TenantSlug, Guid FileId, Guid? UploadKey)
    : ICommand<SupportPublicAttachmentStatusDto>;

/// <summary>
/// Completar una subida del formulario público (plan F33): Storage corre el mismo pipeline que en la consola (tamaño, magic bytes,
/// antivirus, promoción) y la respuesta dice si el archivo se puede adjuntar. Un archivo rechazado no es un error: la página avisa
/// antes de radicar. Uno de otro tenant, con otra clave o sin clave es 422 <c>support.public.attachment_invalid</c>, igual que uno
/// que no existe (plan F32); completar antes de terminar el PUT es 428.
/// </summary>
public sealed class CompletePublicAttachmentHandler(SupportPublicResolver resolver, ISupportAttachmentStorage storage)
    : ICommandHandler<CompletePublicAttachmentCommand, SupportPublicAttachmentStatusDto>
{
    public async Task<SupportPublicAttachmentStatusDto> HandleAsync(
        CompletePublicAttachmentCommand command, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveAsync(command.TenantSlug, cancellationToken);
        if (command.UploadKey is not { } uploadKey || uploadKey == Guid.Empty)
        {
            throw Invalid();
        }

        return await storage.CompleteAsync(tenant.TenantId, command.FileId, uploadKey, cancellationToken) switch
        {
            SupportAttachmentCompletion.Available => new SupportPublicAttachmentStatusDto("available"),
            SupportAttachmentCompletion.Rejected => new SupportPublicAttachmentStatusDto("rejected"),
            SupportAttachmentCompletion.NotUploaded => throw new PreconditionRequiredException(
                SupportErrorCodes.PublicAttachmentNotUploaded, "The file has not been uploaded yet."),
            _ => throw Invalid(),
        };
    }

    internal static SupportDomainException Invalid() =>
        new(SupportErrorCodes.PublicAttachmentInvalid, "The attachment is not valid.");
}
