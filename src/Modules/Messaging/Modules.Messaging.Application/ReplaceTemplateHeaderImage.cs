using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-20: cambia la imagen fija del encabezado de una plantilla guardada. No llama a Meta: el <c>header_handle</c> de
/// Meta es sólo el ejemplo de aprobación y lo que se envía es el enlace de QEP, así que ni el estado ni los componentes se tocan.
/// <c>HeaderImageFileId</c> es nullable para que un cuerpo sin él salga como 422 con su clave, no como 400 del binder.</summary>
public sealed record ReplaceTemplateHeaderImageCommand(Guid TenantId, Guid ConnectionId, Guid TemplateId, Guid? HeaderImageFileId)
    : ICommand<ManagedTemplateDto>;

/// <summary>Forma del cuerpo, con la clave del JSON en <c>errors</c> (<c>OverridePropertyName</c>), igual que al crear.</summary>
public sealed class ReplaceTemplateHeaderImageCommandValidator : AbstractValidator<ReplaceTemplateHeaderImageCommand>
{
    public ReplaceTemplateHeaderImageCommandValidator() =>
        RuleFor(command => command.HeaderImageFileId)
            .Must(fileId => fileId is { } id && id != Guid.Empty)
            .OverridePropertyName("headerImageFileId").WithMessage(TemplateMessages.HeaderImage);
}

/// <summary>Spec 2026-11-20. Orden: autorización, validador, conexión (422 <c>connection_unavailable</c>), plantilla (404), formato del
/// encabezado (422 <c>header_mismatch</c> si no es <c>IMAGE</c>), archivo en uso o retirado (422 <c>header_image_rejected</c>) y revisión
/// del archivo. Después de publicar, un fallo libera sólo la imagen nueva: la anterior queda retirada y referenciada (el historial de mensajes
/// de QEP enlaza a ella), nunca se libera acá. Con la fila guardada la imagen nueva es suya y no se libera.</summary>
public sealed class ReplaceTemplateHeaderImageHandler(
    IMessageTemplateRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ReplaceTemplateHeaderImageCommand> validator,
    IMessagingTemplateImageStore images,
    ITemplateHeaderImageReleases imageReleases,
    ILogger<ReplaceTemplateHeaderImageHandler> logger)
    : ICommandHandler<ReplaceTemplateHeaderImageCommand, ManagedTemplateDto>
{
    private readonly TemplateHeaderImageIntake _intake = new(images, repository, unitOfWork, imageReleases, clock, logger);

    public async Task<ManagedTemplateDto> HandleAsync(ReplaceTemplateHeaderImageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        _ = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var template = await repository.FindAsync(command.TenantId, command.ConnectionId, command.TemplateId, cancellationToken)
            ?? throw TemplateManagementSupport.NotFound(command.TemplateId);
        var described = TemplateCatalog.FromStored(
            template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson, template.HeaderImageUrl);
        if (described.HeaderMedia != TemplateHeaderMedia.Image)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateHeaderMismatch, "Only a template with an image header can change its header image.");
        }

        var fileId = command.HeaderImageFileId!.Value;
        // Cubre la imagen actual, las retiradas de esta plantilla (siguen referenciadas) y la de cualquier otra plantilla del tenant.
        if (await repository.IsHeaderImageInUseAsync(command.TenantId, fileId, cancellationToken))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageRejected, "The header image already belongs to a template.",
                fieldErrors: TemplateHeaderImageIntake.HeaderImageField);
        }

        await _intake.EnsureUsableImageAsync(command.TenantId, fileId, cancellationToken);
        var saved = false;
        try
        {
            var publicUrl = await images.PublishAsync(command.TenantId, fileId, cancellationToken);
            var now = clock.UtcNow;
            if (template.ReplaceHeaderImage(fileId, publicUrl, now) is { } retired)
            {
                repository.AddRetiredHeaderImage(retired);
            }

            audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateHeaderImageReplaced, template.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            saved = true;
        }
        finally
        {
            if (!saved)
            {
                await _intake.ReleaseOrDeferAsync(command.TenantId, fileId);
            }
        }

        var names = await TemplateManagementSupport.MemberNamesAsync(
            memberNames, command.TenantId, template.CreatedByMemberId is { } member ? [member] : [], cancellationToken);
        return ManagedTemplateMapping.ToDto(template, names);
    }
}
