using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-17: lo que Meta acepta como imagen de encabezado y QEP deja subir. Se revisa al abrir la sesión, con lo
/// declarado, y otra vez al crear la plantilla, con lo que Storage guardó.</summary>
public static class TemplateHeaderImagePolicy
{
    public const long MaxSizeBytes = 5 * 1024 * 1024;

    private static readonly string[] AllowedMimeTypes = ["image/jpeg", "image/png"];

    public static bool IsAllowedMimeType(string? mimeType) =>
        mimeType is not null && AllowedMimeTypes.Contains(mimeType.Trim(), StringComparer.OrdinalIgnoreCase);
}

public sealed record CreateTemplateHeaderImageSessionCommand(Guid TenantId, Guid ConnectionId, string? FileName, string? MimeType, long SizeBytes)
    : ICommand<TemplateHeaderImageSessionDto>;

/// <summary>BFF: <c>headers</c> son exactamente las cabeceras que firmó el PUT (<c>Content-Type</c>, <c>If-None-Match</c>), como
/// <see cref="MediaUploadSessionDto"/>. El front las manda tal cual; si el backend cambia lo que firma, el front no se entera.</summary>
public sealed record TemplateHeaderImageSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);

/// <summary>Forma del cuerpo, con las claves del JSON en <c>errors</c> (<c>OverridePropertyName</c>), como
/// <see cref="CreateMediaUploadSessionValidator"/>. Si el formato es JPEG o PNG y cuánto pesa lo decide el handler, con códigos de
/// dominio sin mapa: es un error del formulario, no de un campo que el usuario escribió.</summary>
public sealed class CreateTemplateHeaderImageSessionValidator : AbstractValidator<CreateTemplateHeaderImageSessionCommand>
{
    public CreateTemplateHeaderImageSessionValidator()
    {
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(CreateMediaUploadSessionValidator.MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .OverridePropertyName("fileName");
        RuleFor(command => command.MimeType).NotEmpty().WithMessage("Indica el tipo del archivo.").OverridePropertyName("mimeType");
        RuleFor(command => command.SizeBytes).GreaterThan(0).WithMessage("El archivo está vacío.").OverridePropertyName("sizeBytes");
    }
}

/// <summary>
/// Spec 2026-11-17: firma el PUT de la imagen del encabezado. La autoriza sólo <c>messaging.template.manage</c>, nunca
/// <c>storage.file.*</c>: quien administra plantillas sin permisos de archivos puede crear una con imagen. El orden es el de
/// <see cref="CreateMediaUploadSessionHandler"/>: el 422 de formato o tamaño llega antes del PUT, no después de subir el archivo.
/// </summary>
public sealed class CreateTemplateHeaderImageSessionHandler(
    IMessagingTemplateImageStore images,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<CreateTemplateHeaderImageSessionCommand> validator)
    : ICommandHandler<CreateTemplateHeaderImageSessionCommand, TemplateHeaderImageSessionDto>
{
    public async Task<TemplateHeaderImageSessionDto> HandleAsync(
        CreateTemplateHeaderImageSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        if (!TemplateHeaderImagePolicy.IsAllowedMimeType(command.MimeType))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageUnsupported, "A template header image must be a JPEG or PNG file.");
        }

        if (command.SizeBytes > TemplateHeaderImagePolicy.MaxSizeBytes)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageTooLarge, "A template header image cannot exceed 5 MB.");
        }

        var session = await images.OpenUploadAsync(command.TenantId, command.FileName!, command.MimeType!, command.SizeBytes, cancellationToken);
        return new TemplateHeaderImageSessionDto(session.FileId, session.UploadUrl.AbsoluteUri, session.Headers);
    }
}
