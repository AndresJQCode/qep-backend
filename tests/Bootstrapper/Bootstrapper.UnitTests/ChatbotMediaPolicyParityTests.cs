using Modules.Messaging.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18: el bot manda lo mismo que un asesor por WhatsApp. La política del chatbot es la columna WhatsApp de
/// <see cref="OutboundMediaPolicy"/>, leída en vivo, y Storage tiene que aceptar cada fila para el dueño <see cref="FileOwnerType.ChatbotMedia"/>:
/// si Mensajería agrega un formato y Storage no, esta prueba se pone roja.
/// </summary>
public sealed class ChatbotMediaPolicyParityTests
{
    private static readonly string[] OneOfEachKind = ["a.jpg", "a.mp4", "a.ogg", "a.xlsx"];

    [Fact]
    public void EveryWhatsAppRuleIsAcceptedByStorageForChatbotMedia()
    {
        var policy = new ChatbotMediaPolicy();

        Assert.All(OutboundMediaPolicy.Rules, rule =>
        {
            FileUploadPolicy.ValidateDeclaration("x" + rule.Extension, rule.MimeType, 1, rule.WhatsAppMaxBytes, FileOwnerType.ChatbotMedia);
            Assert.Equal(
                new Modules.Automations.Application.ChatbotMediaRule(rule.Kind.ToString().ToLowerInvariant(), rule.MimeType, rule.WhatsAppMaxBytes),
                policy.RuleFor("x" + rule.Extension));
        });
    }

    // Meta trata un webp como sticker: el asesor no lo manda y el bot tampoco.
    [Fact]
    public void AWebpIsNotSendable()
    {
        Assert.Null(new ChatbotMediaPolicy().RuleFor("a.webp"));
    }

    [Fact]
    public void TheKindsAreTheFlowKinds()
    {
        var policy = new ChatbotMediaPolicy();

        Assert.Equal(
            ["image", "video", "audio", "document"],
            OneOfEachKind.Select(name => policy.RuleFor(name)!.Kind));
    }
}
