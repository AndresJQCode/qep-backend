using Modules.Siigo.Application;
using Modules.Siigo.Infrastructure.Api;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// El token en caché pertenece a las credenciales con las que se pidió, no sólo a la conexión: editar la conexión y poner el usuario
/// y la clave de otra cuenta de Siigo no cambia el <c>connectionId</c>, y servir el token viejo dejaba ver los datos de la cuenta
/// anterior hasta que vencía (unas 24 h).
/// </summary>
public sealed class SiigoTokenCacheTests
{
    private static readonly Guid ConnectionId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly SiigoCredentials AccountA = new(ConnectionId, "api@cuenta-a.test", "clave-a");
    private static readonly SiigoCredentials AccountB = new(ConnectionId, "api@cuenta-b.test", "clave-b");

    private readonly SiigoTokenCache _cache = new(new FixedClock(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void TheTokenIsServedToTheSameCredentials()
    {
        _cache.Set(AccountA, "token-a", TimeSpan.FromHours(24));

        Assert.Equal("token-a", _cache.TryGet(AccountA with { }));
    }

    [Fact]
    public void OtherCredentialsOnTheSameConnectionGetNoToken()
    {
        _cache.Set(AccountA, "token-a", TimeSpan.FromHours(24));

        Assert.Null(_cache.TryGet(AccountB));
    }

    [Fact]
    public void ANewAccessKeyAloneIsEnoughToMissTheToken()
    {
        _cache.Set(AccountA, "token-a", TimeSpan.FromHours(24));

        Assert.Null(_cache.TryGet(AccountA with { AccessKey = "clave-rotada" }));
    }

    // Sin separador entre usuario y clave, "ab" + "c" y "a" + "bc" darían la misma huella.
    [Fact]
    public void MovingCharactersBetweenUserAndKeyChangesTheFingerprint()
    {
        _cache.Set(AccountA with { Username = "ab", AccessKey = "c" }, "token-a", TimeSpan.FromHours(24));

        Assert.Null(_cache.TryGet(AccountA with { Username = "a", AccessKey = "bc" }));
    }

    [Fact]
    public void InvalidatingWithOtherCredentialsDoesNotDropTheCurrentToken()
    {
        _cache.Set(AccountB, "token-b", TimeSpan.FromHours(24));

        _cache.Invalidate(AccountA, "token-b");

        Assert.Equal("token-b", _cache.TryGet(AccountB));
    }

    [Fact]
    public void InvalidatingWithTheSameCredentialsDropsTheToken()
    {
        _cache.Set(AccountA, "token-a", TimeSpan.FromHours(24));

        _cache.Invalidate(AccountA, "token-a");

        Assert.Null(_cache.TryGet(AccountA));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
