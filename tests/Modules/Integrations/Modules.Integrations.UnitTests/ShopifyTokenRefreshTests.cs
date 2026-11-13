using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-11-12 §6.2 y §6.3, las reglas de <see cref="ShopifyTokenRefresh"/>: cuándo hay que refrescar (sin fecha
/// legible, sí), cuándo el token ya venció (sin fecha legible, no) y de qué estados el refresco no puede sacar a una
/// conexión.
/// </summary>
public sealed class ShopifyTokenRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheConstantsAreFiveMinutesAndTwentyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ShopifyTokenRefresh.Interval);
        Assert.Equal(TimeSpan.FromMinutes(20), ShopifyTokenRefresh.Margin);
    }

    [Fact]
    public void FormatIsRoundTripUtc() =>
        Assert.Equal("2026-11-12T12:00:00.0000000+00:00", ShopifyTokenRefresh.Format(Now.ToOffset(TimeSpan.FromHours(-5))));

    [Fact]
    public void IsDueWithoutAReadableExpiryOrWithin20Minutes()
    {
        Assert.True(ShopifyTokenRefresh.IsDue(null, Now));
        Assert.True(ShopifyTokenRefresh.IsDue("x", Now));
        Assert.True(ShopifyTokenRefresh.IsDue(ShopifyTokenRefresh.Format(Now.AddMinutes(20)), Now));
        Assert.False(ShopifyTokenRefresh.IsDue(ShopifyTokenRefresh.Format(Now.AddMinutes(21)), Now));
    }

    [Fact]
    public void IsExpiredOnlyWithAReadableExpiryInThePast()
    {
        Assert.False(ShopifyTokenRefresh.IsExpired(null, Now));
        Assert.False(ShopifyTokenRefresh.IsExpired("x", Now));
        Assert.True(ShopifyTokenRefresh.IsExpired(ShopifyTokenRefresh.Format(Now), Now));
        Assert.False(ShopifyTokenRefresh.IsExpired(ShopifyTokenRefresh.Format(Now.AddSeconds(1)), Now));
    }

    [Fact]
    public void OnlyTokenExpiredAndAppUninstalledAreUnrecoverable()
    {
        Assert.True(ShopifyTokenRefresh.IsUnrecoverable(ConnectionStatus.NeedsAttention, ConnectionFailureCodes.TokenExpired));
        Assert.True(ShopifyTokenRefresh.IsUnrecoverable(ConnectionStatus.NeedsAttention, ConnectionFailureCodes.AppUninstalled));
        Assert.False(ShopifyTokenRefresh.IsUnrecoverable(ConnectionStatus.Paused, ConnectionFailureCodes.TokenExpired));
        Assert.False(ShopifyTokenRefresh.IsUnrecoverable(ConnectionStatus.NeedsAttention, ConnectionFailureCodes.WebhooksNotRegistered));
        Assert.False(ShopifyTokenRefresh.IsUnrecoverable(ConnectionStatus.Active, null));
    }
}
