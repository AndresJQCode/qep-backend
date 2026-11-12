using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerDataPolicyTests
{
    [Fact]
    public void TheConsentUrlIsBuiltFromTheHost()
    {
        var consent = BuyerConsent.Record(Guid.NewGuid(), Guid.NewGuid(), "ana@correo.com", "203.0.113.5", "acme.lulocrm.com", DateTimeOffset.UnixEpoch);
        Assert.Equal("https://acme.lulocrm.com/cuenta/politica", consent.PolicyUrl);
        Assert.Equal("1.0.0", consent.PolicyVersion);
    }
}
