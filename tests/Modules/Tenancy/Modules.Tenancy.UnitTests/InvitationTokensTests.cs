using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

public sealed class InvitationTokensTests
{
    // 32 bytes → 43 caracteres base64url, sin relleno ni caracteres que exijan escape:
    // el token viaja como segmento de path en el link del email.
    [Fact]
    public void GenerateProducesUrlSafeTokensOfThirtyTwoBytes()
    {
        var token = InvitationTokens.Generate();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
    }

    [Fact]
    public void GenerateProducesUniqueTokens()
    {
        var tokens = Enumerable.Range(0, 100)
            .Select(_ => InvitationTokens.Generate())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(100, tokens.Count);
    }

    /// <summary>
    /// Vector conocido de SHA-256 ("abc"): fija el algoritmo y el formato hex minúsculo.
    /// Cambiar cualquiera de los dos deja de matchear los hashes ya persistidos y mata
    /// todos los links de invitación vivos.
    /// </summary>
    [Fact]
    public void HashOfIsSha256InLowercaseHex()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            InvitationTokens.HashOf("abc"));
    }

    [Fact]
    public void HashOfDiffersPerToken()
    {
        Assert.NotEqual(InvitationTokens.HashOf("abc"), InvitationTokens.HashOf("abd"));
    }
}
