using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record OpenPublicAttachmentCommand(string? TenantSlug, string? Name, string? MimeType, long? SizeBytes)
    : ICommand<SupportPublicAttachmentSessionDto>;

/// <summary>Los límites de <see cref="SupportPublicAttachmentRules"/>, cada uno en su campo: un tipo que no se acepta se marca en el
/// nombre (es la extensión la que manda) y en el tipo si el MIME no es el de esa extensión.</summary>
public sealed class OpenPublicAttachmentValidator : AbstractValidator<OpenPublicAttachmentCommand>
{
    public OpenPublicAttachmentValidator()
    {
        RuleFor(command => command.Name)
            .Must(SupportPublicAttachmentRules.IsValidName)
            .WithMessage($"The file name must be a plain name of 1 to {SupportPublicAttachmentRules.NameMaxLength} characters.");
        RuleFor(command => command.Name)
            .Must(name => SupportPublicAttachmentRules.AllowedMimeTypesByExtension.ContainsKey(Path.GetExtension(name!.Trim())))
            .When(command => SupportPublicAttachmentRules.IsValidName(command.Name))
            .WithMessage("Only PDF, Word, Excel, JPG, JPEG, WEBP and PNG files are allowed.");
        RuleFor(command => command.MimeType)
            .Must((command, mimeType) => SupportPublicAttachmentRules.IsAllowedType(command.Name, mimeType))
            .When(command => SupportPublicAttachmentRules.IsValidName(command.Name)
                && SupportPublicAttachmentRules.AllowedMimeTypesByExtension.ContainsKey(Path.GetExtension(command.Name!.Trim())))
            .WithMessage("The media type does not match the file extension.");
        RuleFor(command => command.SizeBytes)
            .Must(size => size is { } value && SupportPublicAttachmentRules.IsValidSize(value))
            .WithMessage($"The file must be between 1 byte and {SupportPublicAttachmentRules.MaxSizeBytes} bytes.");
    }
}

/// <summary>
/// Abrir una subida desde el formulario público (plan F32 y F33). Sin Turnstile —el token es de un solo uso y se gasta al radicar—,
/// con su propio limitador por IP. El <c>uploadKey</c> es un <see cref="Guid.NewGuid"/> (v4, aleatorio) y no un v7 como los ids de
/// Storage, que se adivinan por la hora: es lo único que ata la subida a esta persona, y sólo viaja en esta respuesta. No lee el
/// contexto de ejecución ni la sesión.
/// </summary>
public sealed class OpenPublicAttachmentHandler(
    SupportPublicResolver resolver,
    ISupportAttachmentStorage storage,
    IValidator<OpenPublicAttachmentCommand> validator)
    : ICommandHandler<OpenPublicAttachmentCommand, SupportPublicAttachmentSessionDto>
{
    public async Task<SupportPublicAttachmentSessionDto> HandleAsync(
        OpenPublicAttachmentCommand command, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveAsync(command.TenantSlug, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var uploadKey = Guid.NewGuid();
        var upload = await storage.OpenAsync(
            tenant.TenantId, uploadKey, command.Name!.Trim(), command.MimeType!.Trim(), command.SizeBytes!.Value, cancellationToken);
        return new SupportPublicAttachmentSessionDto(upload.FileId, uploadKey, upload.UploadUrl.AbsoluteUri, upload.Headers);
    }
}
