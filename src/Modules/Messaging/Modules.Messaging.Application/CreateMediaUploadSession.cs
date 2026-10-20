using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record CreateMediaUploadSessionCommand(Guid TenantId, Guid ConversationId, string? FileName, string? MimeType, long SizeBytes)
    : ICommand<MediaUploadSessionDto>;

/// <summary>BFF: <c>headers</c> son exactamente las cabeceras que firmó el PUT. El front las manda tal cual y no necesita saber
/// que R2 exige <c>If-None-Match</c>; si el backend cambia lo que firma, el front no se entera.</summary>
public sealed record MediaUploadSessionDto(Guid UploadId, string UploadUrl, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);

/// <summary>Messaging-media-reply R17: forma del cuerpo. Las claves de <c>errors</c> son las del JSON (<c>OverridePropertyName</c>).
/// Si el tipo es permitido y cuánto pesa lo decide el handler, con códigos de dominio.</summary>
public sealed class CreateMediaUploadSessionValidator : AbstractValidator<CreateMediaUploadSessionCommand>
{
    /// <summary><c>message_media.file_name</c> es varchar(256).</summary>
    public const int MaxFileNameLength = 256;

    public CreateMediaUploadSessionValidator()
    {
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .OverridePropertyName("fileName");
        RuleFor(command => command.MimeType).NotEmpty().WithMessage("Indica el tipo del archivo.").OverridePropertyName("mimeType");
        RuleFor(command => command.SizeBytes).GreaterThan(0).WithMessage("El archivo está vacío.").OverridePropertyName("sizeBytes");
    }
}

/// <summary>
/// Messaging-media-reply R1, R2 y R8: firma un PUT directo del front al bucket privado, en la clave de staging del miembro que
/// llama (tenant, conversación y miembro salen del contexto autenticado, nunca del cuerpo). Sólo carga la conversación: no
/// autoasigna ni mira la ventana, porque pedir una URL no cambia estado; eso lo mide la confirmación con <c>AdmitAsync</c>.
/// </summary>
public sealed class CreateMediaUploadSessionHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IMessagingUploadStaging staging,
    IValidator<CreateMediaUploadSessionCommand> validator)
    : ICommandHandler<CreateMediaUploadSessionCommand, MediaUploadSessionDto>
{
    // CS9124: las dependencias de la guarda no se usan en los métodos. Pedir una URL nunca liquida un envío, así que el acuse
    // (receipts) no se usa acá; la guarda lo exige igual para que ningún camino que sí liquida pueda olvidarlo.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier, receipts);

    public async Task<MediaUploadSessionDto> HandleAsync(CreateMediaUploadSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);

        // La extensión decide el formato y el mime tiene que ser el suyo: un .pdf declarado image/png no se firma. Spec 2026-10-20 D2:
        // formatos y tope son los del canal, para que el 422 llegue antes del PUT y no después de subir 25 MB.
        var extension = OutboundMediaPolicy.ExtensionOf(command.FileName!);
        var channel = target.Conversation.Channel;
        if (OutboundMediaPolicy.FormatOf(extension) is not { } format
            || !string.Equals(OutboundMediaPolicy.MimeTypeOf(format), command.MimeType!.Trim(), StringComparison.OrdinalIgnoreCase)
            || OutboundMediaPolicy.MaxBytesOf(format, channel) is not { } maxBytes)
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "The channel does not accept this file type as is.");
        }

        if (command.SizeBytes > maxBytes)
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaTooLarge, "The file is bigger than the channel allows for its type.");
        }

        var uploadId = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Staging(command.TenantId, command.ConversationId, target.Member, uploadId, extension);
        var mimeType = OutboundMediaPolicy.MimeTypeOf(format);
        var url = await staging.CreateUploadUrlAsync(key, mimeType, cancellationToken);
        return new MediaUploadSessionDto(
            uploadId,
            url.Url.AbsoluteUri,
            url.ExpiresAt,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = mimeType, ["If-None-Match"] = "*" });
    }
}
