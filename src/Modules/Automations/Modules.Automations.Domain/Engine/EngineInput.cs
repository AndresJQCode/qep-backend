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
    /// encola <c>automation_inbox</c>, §5.1); <see cref="ReplyId"/> es el id del botón o de la fila que el cliente tocó.</summary>
    public sealed record CustomerMessage(string Kind, string? Text, string? ReplyId, AttachmentInput? Attachment) : EngineInput;

    /// <summary><see cref="Mapped"/> son las variables que escribe el mapeo del bloque (D-H4); el motor sólo las mezcla si <see cref="Ok"/>.</summary>
    public sealed record HttpResult(string NodeId, bool Ok, int? Status, RunVariables Mapped, string? ErrorCode) : EngineInput;

    public sealed record BusinessHours(string NodeId, bool IsOpen) : EngineInput;
}

/// <summary>El adjunto de un <see cref="EngineInput.CustomerMessage"/>; <see cref="Stored"/> dice si Messaging ya lo tiene guardado.</summary>
public sealed record AttachmentInput(Guid MessageId, string? FileName, string MimeType, bool Stored);

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
