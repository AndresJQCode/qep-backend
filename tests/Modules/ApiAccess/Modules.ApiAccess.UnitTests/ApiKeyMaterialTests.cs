using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.UnitTests;

public sealed class ApiKeyMaterialTests
{
    [Fact]
    public void GeneratedKeyHasTheDocumentedShapeAndAParsableForm()
    {
        var material = ApiKeyMaterial.Generate();

        Assert.Matches("^qep_[0-9A-Za-z]{8}_[0-9A-Za-z]{43}$", material.Plaintext);
        Assert.True(ApiKeyMaterial.TryParse(material.Plaintext, out var prefix, out var secret));
        Assert.Equal(material.Prefix, prefix);
        Assert.True(ApiKeyMaterial.Matches(secret, material.SecretHash));
    }

    [Fact]
    public void ToStringNeverPrintsTheSecret()
    {
        var material = ApiKeyMaterial.Generate();

        Assert.DoesNotContain(material.Secret, material.ToString());
    }

    [Fact]
    public void AWrongSecretDoesNotMatch()
    {
        var material = ApiKeyMaterial.Generate();
        var other = ApiKeyMaterial.Generate();

        Assert.False(ApiKeyMaterial.Matches(other.Secret, material.SecretHash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qep_short_x")]
    [InlineData("qep_live_8fK2mW0xT7pLq9VbN4sRy6Jd1cHzE3uA")]
    [InlineData("qep_abcd1234_con espacios")]
    public void MalformedKeysDoNotParse(string? presented) =>
        Assert.False(ApiKeyMaterial.TryParse(presented, out _, out _));
}
