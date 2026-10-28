namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Un mensaje interactivo de WhatsApp tal como lo arma el motor (D-I1). Es propio de Automations: el adaptador del Bootstrapper lo traduce
/// al <c>InteractiveSend</c> de Messaging, que valida los límites de Meta.
/// </summary>
public abstract record InteractiveMessage(string Body)
{
    /// <summary>Botones de respuesta; cada <c>Id</c> vuelve como <see cref="EngineInput.CustomerMessage.ReplyId"/>.</summary>
    public sealed record Buttons(string Body, IReadOnlyList<(string Id, string Title)> Items) : InteractiveMessage(Body);

    public sealed record List(string Body, string ButtonLabel, IReadOnlyList<(string Id, string Title, string? Description)> Rows)
        : InteractiveMessage(Body);

    /// <summary>Un botón que abre una URL: así sale un Mensaje con <c>urlButton</c>.</summary>
    public sealed record CtaUrl(string Body, string Label, string Url) : InteractiveMessage(Body);
}
