using Modules.Buyers.Application;
using Modules.Buyers.Infrastructure;

namespace Modules.Buyers.IntegrationTests;

public sealed class BuyerPasswordHasherTests
{
    private readonly AspNetCoreBuyerPasswordHasher _hasher = new();

    [Fact]
    public void HashThenVerifySucceeds()
    {
        var hash = _hasher.Hash("correct horse battery");

        Assert.NotEqual("correct horse battery", hash);
        Assert.Equal(BuyerPasswordCheck.Success, _hasher.Verify(hash, "correct horse battery"));
    }

    [Fact]
    public void AWrongPasswordFails()
    {
        var hash = _hasher.Hash("correct horse battery");

        Assert.Equal(BuyerPasswordCheck.Failed, _hasher.Verify(hash, "incorrect horse battery"));
    }

    [Fact]
    public void ANullHashFailsAfterVerifyingTheDummy()
    {
        Assert.Equal(BuyerPasswordCheck.Failed, _hasher.Verify(null, "correct horse battery"));
    }
}
