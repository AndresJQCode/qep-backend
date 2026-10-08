using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Un <see cref="ProtectedSecret"/> con una mitad nula no puede existir (spec 2026-10-07): sin esto,
/// <c>TryUnprotect</c> romperia su contrato de no lanzar ante una fila a medias.
/// </summary>
public sealed class ProtectedSecretTests
{
    [Fact]
    public void ANullKeyIdIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret(null!, [1, 2, 3]));

    [Fact]
    public void ANullCiphertextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret("k1", null!));
}