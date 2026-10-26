namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Lo que hace avanzar una corrida (spec 2026-10-21 §4.3): el arranque, un mensaje del cliente o el resultado de un efecto externo que el
/// motor pidió (<see cref="Effect.CallHttp"/>, <see cref="Effect.CheckBusinessHours"/>). Un resultado trae el <c>NodeId</c> del bloque que
/// lo pidió para que el motor descarte uno que ya no corresponde.
/// </summary>
public abstract record EngineInput
{
    private EngineInput()
    {
    }

    public sealed record Start : EngineInput;

    /// <summary><see cref="Kind"/> es uno de <c>Text | Interactive | Image | Document | Video | Audio | Unsupported</c> (los kinds que
    /// encola <c>automation_inbox</c>, §5.1); <see cref="ReplyId"/> es el id del botón o de la fila que el cliente tocó.
    /// <see cref="ReplyToMessageId"/> (final review F1 de la spec 2026-10-26): el mensaje de QEP que cita el entrante —el del botón tocado—,
    /// o <c>null</c> si no cita ninguno o Mensajería no lo encontró.</summary>
    public sealed record CustomerMessage(string Kind, string? Text, string? ReplyId, AttachmentInput? Attachment, Guid? ReplyToMessageId = null)
        : EngineInput;

    /// <summary><see cref="Mapped"/> son las variables que escribe el mapeo del bloque (D-H4); el motor sólo las mezcla si <see cref="Ok"/>.
    /// <see cref="Outcome"/> y <see cref="ConnectionName"/> (spec 2026-10-24 §4.2) el motor no los lee, como tampoco <see cref="Status"/> ni
    /// <see cref="ErrorCode"/>: viajan para que el bucle de efectos explique un traspaso que vino de este resultado.</summary>
    public sealed record HttpResult(
        string NodeId, bool Ok, int? Status, RunVariables Mapped, string? ErrorCode, string? Outcome = null, string? ConnectionName = null)
        : EngineInput;

    public sealed record BusinessHours(string NodeId, bool IsOpen) : EngineInput;

    /// <summary>Spec 2026-10-26 D-2 y D-7: la respuesta a <see cref="Effect.CheckDataConsent"/>. <see cref="Accepted"/> dice si el cliente
    /// tiene hoy una aceptación vigente; <see cref="CanRecord"/> es <c>false</c> si no hay cliente al que registrarle la respuesta.</summary>
    public sealed record DataConsent(string NodeId, bool Accepted, bool CanRecord = true) : EngineInput;
}

/// <summary>El adjunto de un <see cref="EngineInput.CustomerMessage"/>; <see cref="Stored"/> dice si Messaging ya lo tiene guardado.
/// <see cref="Url"/> es el enlace firmado por 15 min (D-M8) que el procesador arma antes del paso cuando la corrida espera en un Pedir
/// adjunto y el archivo ya está en R2: va dentro de la entrada y no se completa después porque el mismo recorrido puede seguir hasta un
/// bloque que lee <c>{saveTo.url}</c>.</summary>
public sealed record AttachmentInput(Guid MessageId, string? FileName, string MimeType, bool Stored, string? Url = null);

/// <summary>Los <see cref="EngineInput.CustomerMessage.Kind"/> que encola <c>automation_inbox</c> (§5.1): los nombres del
/// <c>MessageKind</c> de Messaging, que Automations no referencia.</summary>
public static class CustomerMessageKinds
{
    public const string Text = "Text";
    public const string Interactive = "Interactive";
    public const string Image = "Image";
    public const string Document = "Document";
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string Unsupported = "Unsupported";
}
