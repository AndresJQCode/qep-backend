using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Messaging-media-reply R3: confirma el adjunto que el front dejó en el staging. <c>FileName</c> es el que la persona vio
/// al elegir el archivo; el staging se reconstruye con su extensión, el <c>uploadId</c> y el contexto autenticado.</summary>
public sealed record SendMediaMessageCommand(
    Guid TenantId, Guid ConversationId, Guid? ClientId, Guid? UploadId, string? FileName, string? Caption, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>R11 y R17: forma del cuerpo. Las claves de <c>errors</c> son las del JSON (<c>OverridePropertyName</c>). Si el tipo es
/// permitido y cuánto pesa lo decide el handler, con códigos de dominio.</summary>
public sealed class SendMediaMessageValidator : AbstractValidator<SendMediaMessageCommand>
{
    /// <summary>El tope de Meta para el pie de una imagen o un documento, y <c>messages.caption</c> es varchar(1024).</summary>
    public const int MaxCaptionLength = 1024;

    public SendMediaMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.UploadId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("uploadId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(CreateMediaUploadSessionValidator.MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .OverridePropertyName("fileName");
        RuleFor(command => command.Caption)
            .MaximumLength(MaxCaptionLength).WithMessage("El texto que acompaña al archivo puede tener hasta 1024 caracteres.")
            .OverridePropertyName("caption");
    }
}
