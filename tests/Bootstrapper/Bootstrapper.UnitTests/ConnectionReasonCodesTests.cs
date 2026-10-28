using Bootstrapper;
using Modules.Integrations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-24 D-T10 y §3.4: el único traductor del motivo de Integrations a los códigos que viajan al hilo.</summary>
public sealed class ConnectionReasonCodesTests
{
    [Theory]
    [InlineData(ConnectionUnavailableReason.NotFound, "not_found")]
    [InlineData(ConnectionUnavailableReason.Paused, "paused")]
    [InlineData(ConnectionUnavailableReason.NeedsAttention, "needs_attention")]
    [InlineData(ConnectionUnavailableReason.ProviderHidden, "provider_hidden")]
    [InlineData(ConnectionUnavailableReason.ProviderDisabled, "provider_disabled")]
    [InlineData(ConnectionUnavailableReason.SecretUnreadable, "secret_unreadable")]
    public void EveryReasonHasItsCode(ConnectionUnavailableReason reason, string code) =>
        Assert.Equal(code, ConnectionReasonCodes.Of(reason));

    // Un valor nuevo sin código rompe esta prueba, no el hilo.
    [Fact]
    public void EveryEnumValueIsMapped() =>
        Assert.All(Enum.GetValues<ConnectionUnavailableReason>(), reason => Assert.Matches("^[a-z_]+$", ConnectionReasonCodes.Of(reason)));

    [Fact]
    public void TheAdapterCodesAreTheSpecOnes() =>
        Assert.Equal(("wrong_provider", "incomplete"), (ConnectionReasonCodes.WrongProvider, ConnectionReasonCodes.Incomplete));
}
