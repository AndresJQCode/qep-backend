using BuildingBlocks.Application;
using FluentValidation;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §6.1: el endpoint copia <c>file</c> a memoria (≤ 17 MB por el tope del cuerpo) para que
/// el handler calcule el hash, revise la cabecera y suba dos veces (R2 y Meta) sin volver a leer el request.</summary>
public sealed record SendAudioMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, byte[]? Audio, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>Spec 2026-10-11 §6.2: <c>clientId</c> GUID no vacío; <c>replyTo</c>, si viene, no vacío; <c>file</c> de
/// 1 B a <see cref="MaxAudioBytes"/> y OGG/Opus por contenido (<see cref="OggOpusSniffer"/>). Las claves de
/// <c>errors</c> son las del form (<c>OverridePropertyName</c>).</summary>
public sealed class SendAudioMessageValidator : AbstractValidator<SendAudioMessageCommand>
{
    /// <summary>§3: el tope de Meta para un audio.</summary>
    public const int MaxAudioBytes = 16 * 1024 * 1024;

    public SendAudioMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
        // Primero el tamaño: un archivo vacío o enorme no se inspecciona.
        RuleFor(command => command.Audio)
            .Cascade(CascadeMode.Stop)
            .Must(audio => audio is { Length: > 0 and <= MaxAudioBytes }).WithMessage("Graba una nota de voz de hasta 16 MB.")
            .Must(audio => OggOpusSniffer.IsOggOpus(audio!)).WithMessage("La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.")
            .OverridePropertyName("file");
    }
}
