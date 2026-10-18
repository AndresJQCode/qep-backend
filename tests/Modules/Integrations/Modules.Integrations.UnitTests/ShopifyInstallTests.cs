using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 y §9, el agregado <see cref="ShopifyInstall"/>: tenant y membresía van juntos (A) o ninguno (B);
/// el <c>state</c> se canjea una sola vez y dentro de los 15 minutos; el canje mueve el vencimiento a +7 días (D-S16) y
/// rechaza lo que no cabe en sus columnas, en vez de dejar que la base responda 500.
/// </summary>
public sealed class ShopifyInstallTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string State = "state-0123456789abcdefghijklmnopqrstuvwxyzABC";

    private const string RedirectUri = "https://qep.qcode.co/shopify/callback";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProtectedSecret Token = new("test", [1, 2, 3]);

    private static ShopifyInstall Start(Guid? tenantId = null, Guid? memberId = null) =>
        ShopifyInstall.Start(Guid.NewGuid(), Shop, State, RedirectUri, tenantId, memberId, Now);

    [Fact]
    public void StartFromQepCarriesTenantAndMembership()
    {
        var tenantId = Guid.NewGuid();
        var memberId = Guid.NewGuid();

        var install = Start(tenantId, memberId);

        Assert.Equal(tenantId, install.InitiatedTenantId);
        Assert.Equal(memberId, install.InitiatedBy);
        Assert.Equal(Now.AddMinutes(15), install.ExpiresAt);
        Assert.False(install.IsExchanged);
    }

    [Fact]
    public void StartFromShopifyCarriesNeither()
    {
        var install = Start();

        Assert.Null(install.InitiatedTenantId);
        Assert.Null(install.InitiatedBy);
    }

    [Fact]
    public void StartWithTenantButWithoutMembershipThrows() =>
        Assert.Throws<ArgumentException>(() => Start(tenantId: Guid.NewGuid()));

    [Fact]
    public void StartWithMembershipButWithoutTenantThrows() =>
        Assert.Throws<ArgumentException>(() => Start(memberId: Guid.NewGuid()));

    [Fact]
    public void CanBeExchangedWhileFreshAndWithoutToken()
    {
        var install = Start();

        Assert.True(install.CanBeExchanged(Now.AddMinutes(14)));
        Assert.False(install.CanBeExchanged(Now.AddMinutes(15)));
    }

    [Fact]
    public void MarkExchangedStoresTheShopAndExtendsSevenDays()
    {
        var install = Start();
        var exchangedAt = Now.AddMinutes(3);

        install.MarkExchanged("12345678", "Mi Tienda", "read_orders,read_customers", Token, exchangedAt);

        Assert.True(install.IsExchanged);
        Assert.Equal("12345678", install.ShopId);
        Assert.Equal("Mi Tienda", install.ShopName);
        Assert.Equal("read_orders,read_customers", install.Scopes);
        Assert.Equal(Token.Ciphertext, install.ProtectedToken);
        Assert.Equal("test", install.KeyId);
        Assert.Equal(exchangedAt.AddDays(7), install.ExpiresAt);
        Assert.False(install.CanBeExchanged(exchangedAt));
    }

    [Fact]
    public void MarkExchangedTwiceThrows()
    {
        var install = Start();
        install.MarkExchanged("1", "Mi Tienda", "read_orders", Token, Now);

        Assert.Throws<InvalidOperationException>(() => install.MarkExchanged("1", "Mi Tienda", "read_orders", Token, Now));
    }

    [Fact]
    public void MarkExchangedOnAnExpiredRowThrows()
    {
        var install = Start();

        Assert.Throws<InvalidOperationException>(() =>
            install.MarkExchanged("1", "Mi Tienda", "read_orders", Token, Now.AddMinutes(16)));
        Assert.False(install.IsExchanged);
    }

    // El nombre sólo se muestra: se recorta. El id y los scopes no se pueden recortar sin mentir, así que se rechazan; el
    // gateway ya los filtra antes (ShopifyGatewayException Invalid), esto es la última defensa contra un 500 de la base.
    [Fact]
    public void MarkExchangedTrimsTheNameButRejectsAnIdOrScopesThatDoNotFit()
    {
        var install = Start();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            install.MarkExchanged(new string('1', ShopifyInstall.ShopIdMaxLength + 1), "Mi Tienda", "read_orders", Token, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            install.MarkExchanged("1", "Mi Tienda", new string('a', ShopifyInstall.ScopesMaxLength + 1), Token, Now));
        Assert.False(install.IsExchanged);

        install.MarkExchanged("1", new string('n', ShopifyInstall.ShopNameMaxLength + 10), "read_orders", Token, Now);

        Assert.Equal(ShopifyInstall.ShopNameMaxLength, install.ShopName!.Length);
    }

    // El reclamo reemplaza el state de Shopify por uno con prefijo, una sola vez y sólo mientras esté vigente.
    [Fact]
    public void ClaimReplacesTheStateOnceWhileExchangeable()
    {
        var install = Start();
        var claim = ShopifyInstall.ClaimedStatePrefix + "abc";

        install.Claim(claim, Now.AddMinutes(1));

        Assert.True(install.IsClaimed);
        Assert.Equal(claim, install.State);
        Assert.True(install.CanBeExchanged(Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => install.Claim(ShopifyInstall.ClaimedStatePrefix + "def", Now.AddMinutes(1)));
    }

    [Fact]
    public void ClaimExpiredOrWithoutThePrefixThrows()
    {
        var install = Start();

        Assert.Throws<InvalidOperationException>(() => install.Claim(ShopifyInstall.ClaimedStatePrefix + "abc", Now.AddMinutes(15)));
        Assert.Throws<ArgumentException>(() => install.Claim("abc", Now));
        Assert.Throws<ArgumentException>(() => install.Claim(ShopifyInstall.ClaimedStatePrefix + new string('a', 64), Now));
        Assert.False(install.IsClaimed);
    }

    [Fact]
    public void ToStringDoesNotPrintTheStateOrTheToken()
    {
        var install = Start();
        install.MarkExchanged("1", "Mi Tienda", "read_orders", Token, Now);

        Assert.DoesNotContain(State, install.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte", install.ToString(), StringComparison.Ordinal);
    }
}
