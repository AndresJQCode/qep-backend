using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Un <see cref="ProtectedSecret"/> con una mitad nula no puede existir (spec 2026-10-08, «Secreto en
/// reposo»; viene de 6612298): sin esto, <c>TryUnprotect</c> rompería su contrato de no lanzar ante
/// una fila a medias.
/// </summary>
public sealed class ProtectedSecretTests
{
    [Fact]
    public void ANullKeyIdIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret(null!, [1, 2, 3]));

    [Fact]
    public void ANullCiphertextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret("k1", null!));

    [Fact]
    public void ToStringShowsTheKeyButNeverTheBytes() =>
        Assert.Equal("ProtectedSecret { KeyId = k1 }", new ProtectedSecret("k1", [1, 2, 3]).ToString());
}
