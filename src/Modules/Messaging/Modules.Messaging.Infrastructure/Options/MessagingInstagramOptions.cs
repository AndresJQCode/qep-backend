namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-14 §9 (V-1): la parte de <c>Meta:Instagram</c> que Messaging usa: el secreto que firma el webhook de
/// Instagram y la versión de Graph. El validador vive en Integrations (<c>MetaInstagramOptionsValidator</c>). Vacío = ausente: el
/// webhook se verifica entonces sólo con el secreto de <c>Meta:App</c>.</summary>
public sealed class MessagingInstagramOptions
{
    public const string SectionName = "Meta:Instagram";

    public string? AppSecret { get; set; }

    public string GraphApiVersion { get; set; } = "v24.0";
}
