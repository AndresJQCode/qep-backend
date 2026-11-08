using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

public sealed class CompanyDocumentLabelsTests
{
    [Theory]
    [InlineData("NIT", "NIT")]
    [InlineData("CC", "CC")]
    [InlineData("CE", "CE")]
    [InlineData("PASAPORTE", "Pasaporte")]
    [InlineData("NIF", "NIF")]
    [InlineData("NIE", "NIE")]
    [InlineData("TAX_ID", "Identificación tributaria")]
    [InlineData(null, "NIT")]
    [InlineData("XYZ", "XYZ")]
    public void LabelsTheDocumentByItsWireCode(string? wire, string expected) =>
        Assert.Equal(expected, CompanyDocumentLabels.For(wire));
}
