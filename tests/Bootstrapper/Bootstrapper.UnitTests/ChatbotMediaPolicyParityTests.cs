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
    private static readonly string[] PresentationAndText = ["a.ppt", "a.pptx", "a.txt"];

    [Fact]
    public void EveryWhatsAppRuleIsAcceptedByStorageForChatbotMedia()
    {
        var policy = new ChatbotMediaPolicy();

        // El sticker no es un tipo del flujo del chatbot (ChatbotMediaKinds): sólo lo manda un asesor.
        Assert.All(OutboundMediaPolicy.Rules.Where(rule => rule.Kind != Modules.Messaging.Domain.MessageKind.Sticker), rule =>
        {
            FileUploadPolicy.ValidateDeclaration("x" + rule.Extension, rule.MimeType, 1, rule.WhatsAppMaxBytes, FileOwnerType.ChatbotMedia);
            Assert.Equal(
                new Modules.Automations.Application.ChatbotMediaRule(rule.Kind.ToString().ToLowerInvariant(), rule.MimeType, rule.WhatsAppMaxBytes),
                policy.RuleFor("x" + rule.Extension));
        });
    }

    // Meta trata un webp como sticker: lo manda el asesor, pero el flujo del chatbot no tiene ese tipo.
    [Fact]
    public void AWebpIsNotSendableByTheBot()
    {
        var policy = new ChatbotMediaPolicy();

        Assert.Null(policy.RuleFor("a.webp"));
        Assert.Null(ChatbotMediaPolicy.KindOfFileName("a.webp"));
    }

    [Fact]
    public void ThePresentationAndTextDocumentsAreDocuments()
    {
        var policy = new ChatbotMediaPolicy();

        Assert.Equal(["document", "document", "document"], PresentationAndText.Select(name => policy.RuleFor(name)!.Kind));
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
