using System.Security.Cryptography;
using System.Text;
using Modules.Wms.Application;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class RequestHasherTests
{
    private sealed record Body(long Version, string? Note) : IIdempotentRequest;

    [Fact]
    public void TwoEqualBodiesHashTheSame() =>
        Assert.Equal(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/a", new Body(1, "x")));

    [Fact]
    public void ADifferentPathOrBodyHashesDifferently()
    {
        Assert.NotEqual(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/b", new Body(1, "x")));
        Assert.NotEqual(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/a", new Body(2, "x")));
    }

    [Fact]
    public void TheHashIsSixtyFourLowercaseHexCharacters() =>
        Assert.Matches("^[0-9a-f]{64}$", RequestHasher.Hash("POST", "/a", null));

    [Fact]
    public void TheHashIsTheSha256OfMethodPathAndJsonBody()
    {
        // La forma exacta es contrato con los recibos ya guardados: cambiarla invalida las repeticiones en vuelo.
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("POST /a\nnull")));
        Assert.Equal(expected, RequestHasher.Hash("POST", "/a", null));
        Assert.NotEqual(RequestHasher.Hash("POST", "/a", null), RequestHasher.Hash("POST", "/a", new Body(0, null)));
    }
}
