using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>Las claves del 422 de una lista de precios: <c>Name</c>, <c>DiscountPercent</c>, <c>Visibility</c>,
/// <c>CategoryIds</c> y <c>CategoryIds[1]</c> (repetida), como dice el contrato.</summary>
public sealed class PriceListWriteRulesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static CreatePriceListCommand Create(
        string name = "Mayoristas",
        decimal discountPercent = 15m,
        string visibility = "Categories",
        IReadOnlyList<Guid>? categoryIds = null) =>
        new(Tenant, name, discountPercent, visibility, categoryIds ?? [Guid.CreateVersion7()]);

    private static string[] FailedProperties(CreatePriceListCommand command) =>
        new CreatePriceListValidator().Validate(command).Errors.Select(error => error.PropertyName).Distinct().ToArray();

    private static string[] FailedProperties(PreviewPriceListCommand command) =>
        new PreviewPriceListValidator().Validate(command).Errors.Select(error => error.PropertyName).Distinct().ToArray();

    [Fact]
    public void AValidCommandPasses()
    {
        Assert.Empty(FailedProperties(Create()));
        Assert.Empty(FailedProperties(Create(visibility: "AllActive", categoryIds: [])));
        Assert.Empty(FailedProperties(Create(discountPercent: 0m)));
        Assert.Empty(FailedProperties(Create(discountPercent: 100m)));
        Assert.Empty(FailedProperties(Create(discountPercent: 12.50m)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("10.005")]
    public void DiscountRulesFailOnTheField(string discount)
    {
        var command = Create(discountPercent: decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(["DiscountPercent"], FailedProperties(command));
        Assert.Equal(
            ["DiscountPercent"],
            FailedProperties(new PreviewPriceListCommand(Tenant, command.DiscountPercent, "AllActive", [])));
    }

    [Fact]
    public void CategoriesVisibilityNeedsAtLeastOneId()
    {
        Assert.Equal(["CategoryIds"], FailedProperties(Create(categoryIds: [])));
        Assert.Equal(["CategoryIds"], FailedProperties(new CreatePriceListCommand(Tenant, "Mayoristas", 15m, "Categories", null!)));
        Assert.Equal(["CategoryIds"], FailedProperties(new PreviewPriceListCommand(Tenant, 15m, "Categories", [])));
    }

    [Fact]
    public void ARepeatedCategoryFailsOnTheSecond()
    {
        var category = Guid.CreateVersion7();

        Assert.Equal(["CategoryIds[1]"], FailedProperties(Create(categoryIds: [category, category])));
    }

    [Fact]
    public void MoreThanTwoHundredCategoriesFail()
    {
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToList();

        Assert.Equal(["CategoryIds"], FailedProperties(Create(categoryIds: ids)));
    }

    [Theory]
    [InlineData("Everything")]
    [InlineData("")]
    [InlineData("1")]
    public void UnknownVisibilityFails(string visibility)
    {
        Assert.Equal(["Visibility"], FailedProperties(Create(visibility: visibility, categoryIds: [])));
    }

    [Fact]
    public void AllActiveIgnoresTheCategories()
    {
        var category = Guid.CreateVersion7();

        Assert.Empty(FailedProperties(Create(visibility: "AllActive", categoryIds: [category, category])));
        Assert.Empty(FailedProperties(new CreatePriceListCommand(Tenant, "Mayoristas", 15m, "AllActive", null!)));
    }

    [Fact]
    public void NameIsRequiredAndAtMost120()
    {
        Assert.Equal(["Name"], FailedProperties(Create(name: "   ")));
        Assert.Equal(["Name"], FailedProperties(Create(name: new string('a', 121))));
        Assert.Empty(FailedProperties(Create(name: "  " + new string('a', 120) + "  ")));
        Assert.Equal(
            ["Name"],
            new UpdatePriceListValidator()
                .Validate(new UpdatePriceListCommand(Tenant, Guid.CreateVersion7(), 1, "", 15m, "AllActive", []))
                .Errors.Select(error => error.PropertyName).Distinct().ToArray());
    }
}
