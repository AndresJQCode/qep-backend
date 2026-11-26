using Modules.Tenancy.Application;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-24: el secreto que sólo viaja en el correo de la invitación y prueba el buzón invitado.</summary>
public sealed class DistributorEmailProofsTests
{
    [Fact]
    public void IssueGivesAProofAndItsHash()
    {
        var first = DistributorEmailProofs.Issue();
        var second = DistributorEmailProofs.Issue();

        Assert.Equal(InvitationTokens.HashOf(first.Proof), first.Hash);
        Assert.NotEqual(first.Proof, first.Hash);
        Assert.NotEqual(first.Proof, second.Proof);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ProvesIsFalseForABlankProof(string? emailProof)
    {
        var distributor = new DistributorWorld().SeedInvited(emailProofHash: DistributorEmailProofs.Issue().Hash);

        Assert.False(DistributorEmailProofs.Proves(distributor, emailProof, distributor.Email));
    }

    [Fact]
    public void ProvesMatchesTheIssuedProofAndTheInvitedEmail()
    {
        var (proof, hash) = DistributorEmailProofs.Issue();
        var distributor = new DistributorWorld().SeedInvited(emailProofHash: hash);

        Assert.True(DistributorEmailProofs.Proves(distributor, proof, "  Ana@Distri.CO "));
        Assert.False(DistributorEmailProofs.Proves(distributor, proof, "otra@distri.co"));
        Assert.False(DistributorEmailProofs.Proves(distributor, proof, null));
        Assert.False(DistributorEmailProofs.Proves(distributor, DistributorEmailProofs.Issue().Proof, distributor.Email));
        // El hash guardado no es la prueba: leer la base no alcanza.
        Assert.False(DistributorEmailProofs.Proves(distributor, hash, distributor.Email));
    }
}
