using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Regla de RequesterDisplayName (plan, contrato BFF): nombres y apellidos; si no, razón social; si no, «NIT <número>».
public sealed class TicketRequesterTests
{
    private static readonly TicketRequester Empty = new(null, null, null, null, null, null, null, null, "CO", null);

    [Fact]
    public void ThePersonNameJoinsFirstAndLastNames() =>
        Assert.Equal("Ana María Gómez", (Empty with { FirstNames = " Ana María ", LastNames = "Gómez " }).DisplayName());

    [Fact]
    public void FirstNamesAloneAreEnough() =>
        Assert.Equal("Ana", (Empty with { FirstNames = "Ana" }).DisplayName());

    [Fact]
    public void WithoutNamesTheBusinessNameIsShown() =>
        Assert.Equal(
            "Ferretería El Tornillo SAS",
            (Empty with { BusinessName = "Ferretería El Tornillo SAS", DocumentType = "NIT", DocumentNumber = "900123456" }).DisplayName());

    [Fact]
    public void ANitWithoutBusinessNameShowsTheNumber()
    {
        var requester = Empty with { DocumentType = "NIT", DocumentNumber = "900123456" };

        Assert.Equal("NIT 900123456", requester.DisplayName());
        Assert.False(requester.HasName);
    }

    [Fact]
    public void NothingToShowIsEmpty() =>
        Assert.Equal(string.Empty, (Empty with { DocumentType = "CC", DocumentNumber = "1020304050" }).DisplayName());

    // Plan D6: el alta manual exige nombres, o razón social con NIT.
    [Fact]
    public void HasNameNeedsFirstNamesOrABusinessNameWithNit()
    {
        Assert.True((Empty with { FirstNames = "Ana" }).HasName);
        Assert.True((Empty with { BusinessName = "El Tornillo", DocumentType = "NIT", DocumentNumber = "900123456" }).HasName);
        Assert.False((Empty with { BusinessName = "El Tornillo", DocumentType = "CC", DocumentNumber = "1020304050" }).HasName);
        Assert.False((Empty with { LastNames = "Gómez" }).HasName);
        Assert.False(Empty.HasName);
    }
}
