using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;

namespace Modules.Suppliers.UnitTests;

public sealed class SupplierStatusFilterTests
{
    [Theory]
    [InlineData("active", SupplierStatusFilter.Active)]
    [InlineData(" INACTIVE ", SupplierStatusFilter.Inactive)]
    public void ParseAcceptsActiveAndInactiveIgnoringCase(string value, SupplierStatusFilter expected) =>
        Assert.Equal(expected, SupplierStatusFilterParser.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTreatsBlankAsNoFilter(string? value) =>
        Assert.Null(SupplierStatusFilterParser.Parse(value));

    [Theory]
    [InlineData("activo")]
    [InlineData("all")]
    public void ParseRejectsAnythingElseWithStatusFilterInvalid(string value)
    {
        var error = Assert.Throws<SuppliersDomainException>(() => SupplierStatusFilterParser.Parse(value));

        Assert.Equal("suppliers.supplier.status_filter_invalid", error.Code);
    }
}
