using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-21 D5–D10: una prueba por regla de <see cref="WhatsAppChannelSelection.Resolve"/>, en el orden en que
/// la función las evalúa.
/// </summary>
public sealed class WhatsAppChannelSelectionTests
{
    [Fact]
    public void NoChannelIsNoChannel()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve([], saved: null, requested: null));

        Assert.Equal("quotation.whatsapp.no_channel", error.Code);
    }

    [Fact]
    public void AValidChoiceIsUsed()
    {
        var chosen = WhatsAppChannels.Any();
        var other = WhatsAppChannels.Meta();

        var resolved = WhatsAppChannelSelection.Resolve([chosen, other], chosen.ConnectionId, requested: null);

        Assert.Same(chosen, resolved.Channel);
        Assert.False(resolved.Remember);
    }

    [Fact]
    public void AValidChoiceAcceptsItsOwnId()
    {
        var chosen = WhatsAppChannels.Any();
        var other = WhatsAppChannels.Meta();

        var resolved = WhatsAppChannelSelection.Resolve([chosen, other], chosen.ConnectionId, chosen.ConnectionId);

        Assert.Same(chosen, resolved.Channel);
        Assert.False(resolved.Remember);
    }

    [Fact]
    public void AValidChoiceRejectsAnotherId()
    {
        var chosen = WhatsAppChannels.Any();
        var other = WhatsAppChannels.Meta();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve([chosen, other], chosen.ConnectionId, other.ConnectionId));

        Assert.Equal("quotation.whatsapp.channel_already_set", error.Code);
    }

    [Fact]
    public void TheOnlyChannelIsUsedWithoutRemembering()
    {
        var only = WhatsAppChannels.Meta();

        var withoutId = WhatsAppChannelSelection.Resolve([only], saved: null, requested: null);
        var withItsId = WhatsAppChannelSelection.Resolve([only], saved: null, requested: only.ConnectionId);

        Assert.Same(only, withoutId.Channel);
        Assert.False(withoutId.Remember);
        Assert.Same(only, withItsId.Channel);
        Assert.False(withItsId.Remember);
    }

    [Fact]
    public void TheOnlyChannelWithAnotherIdIsUnavailable()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve([WhatsAppChannels.Meta()], saved: null, requested: Guid.CreateVersion7()));

        Assert.Equal("quotation.whatsapp.channel_unavailable", error.Code);
    }

    [Fact]
    public void SeveralWithoutChoiceRequireAConnectionId()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve(
                [WhatsAppChannels.Any(), WhatsAppChannels.Meta()], saved: null, requested: null));

        Assert.Equal("quotation.whatsapp.channel_required", error.Code);
    }

    [Fact]
    public void SeveralWithAnUnknownIdIsUnavailable()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve(
                [WhatsAppChannels.Any(), WhatsAppChannels.Meta()], saved: null, requested: Guid.CreateVersion7()));

        Assert.Equal("quotation.whatsapp.channel_unavailable", error.Code);
    }

    [Fact]
    public void SeveralWithAnAvailableIdIsRemembered()
    {
        var wanted = WhatsAppChannels.Meta();

        var resolved = WhatsAppChannelSelection.Resolve(
            [WhatsAppChannels.Any(), wanted], saved: null, requested: wanted.ConnectionId);

        Assert.Same(wanted, resolved.Channel);
        Assert.True(resolved.Remember);
        Assert.Null(resolved.PreviousChoice);
    }

    // Review Focus 1: una elección guardada cuya conexión ya no está disponible cuenta como si no hubiera ninguna, pero
    // la fila vencida es la que la nueva elección tiene que reemplazar.
    [Fact]
    public void AStaleChoiceIsTreatedAsNone()
    {
        var stale = Guid.CreateVersion7();
        var first = WhatsAppChannels.Any();
        var second = WhatsAppChannels.Meta();

        var withoutId = Assert.Throws<QuotationsDomainException>(() =>
            WhatsAppChannelSelection.Resolve([first, second], stale, requested: null));
        var resolved = WhatsAppChannelSelection.Resolve([first, second], stale, second.ConnectionId);

        Assert.Equal("quotation.whatsapp.channel_required", withoutId.Code);
        Assert.Same(second, resolved.Channel);
        Assert.True(resolved.Remember);
        Assert.Equal(stale, resolved.PreviousChoice);
    }
}
