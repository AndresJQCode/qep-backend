using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

public sealed class UnsupportedMessageReasonsTests
{
    [Fact]
    public void AnImportedUnsupportedTypeExplainsTheHistoryLimit() =>
        Assert.Equal(
            "Este mensaje no se pudo importar del historial: WhatsApp no entrega este tipo. Puedes verlo en el celular.",
            UnsupportedMessageReasons.For(131051, imported: true));

    [Fact]
    public void ALiveUnsupportedTypeKeepsAskingThePersonToResend() =>
        Assert.Equal(
            "WhatsApp no admite este tipo de mensaje. Pídele a la persona que lo envíe como texto, imagen o documento.",
            UnsupportedMessageReasons.For(131051, imported: false));

    [Fact]
    public void AnImportedUnavailableMessageIsUnchanged() =>
        Assert.Equal(
            "WhatsApp no entregó el contenido de este mensaje. Pídele a la persona que lo envíe de nuevo.",
            UnsupportedMessageReasons.For(131060, imported: true));
}
