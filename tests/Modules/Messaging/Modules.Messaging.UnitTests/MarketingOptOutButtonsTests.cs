using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Decisión del owner 2026-10-12 (P1): el opt-out de marketing por el botón de respuesta rápida de la plantilla. La coincidencia
/// ignora mayúsculas, tildes y espacios de los bordes, y mira tanto el texto del botón como su payload.</summary>
public sealed class MarketingOptOutButtonsTests
{
    private static MarketingOptOutButtons Over(params string[]? texts) =>
        new(Microsoft.Extensions.Options.Options.Create(new MessagingMarketingOptOutOptions { ButtonTexts = texts is { Length: > 0 } ? texts : null }));

    [Theory]
    [InlineData("Detener promociones")]
    [InlineData("  detener PROMOCIONES ")]
    [InlineData("Stop promotions")]
    [InlineData("STOP PROMOTIONS")]
    public void TheDefaultTextsMatchIgnoringCaseAndEdgeSpaces(string text) =>
        Assert.True(Over().Matches(new QuickReplyButton(text, null)));

    [Fact]
    public void AccentsDoNotMatter() =>
        Assert.True(Over("Detener promociónes").Matches(new QuickReplyButton("detener promociones", null)));

    [Fact]
    public void ThePayloadAlsoMatches() =>
        Assert.True(Over().Matches(new QuickReplyButton("Otro texto", "detener promociones")));

    [Theory]
    [InlineData("Confirmar", null)]
    [InlineData("Confirmar", "ok")]
    [InlineData("Detener promociones ahora", null)]
    [InlineData("", null)]
    public void AnotherButtonDoesNotMatch(string text, string? payload) =>
        Assert.False(Over().Matches(new QuickReplyButton(text, payload)));

    [Fact]
    public void ConfiguredTextsReplaceTheDefaults()
    {
        var buttons = Over("Baja");

        Assert.True(buttons.Matches(new QuickReplyButton("baja", null)));
        Assert.False(buttons.Matches(new QuickReplyButton("Detener promociones", null)));
    }
}
