using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class OptionDefinitionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateRejectsDuplicateValueIgnoringCaseAndEdgeSpaces()
    {
        var ex = Assert.Throws<CatalogDomainException>(() =>
            OptionDefinition.Create(TenantId, "Talla", ["M", " m "], true, Now));

        Assert.Equal("catalog.option.value_duplicate", ex.Code);
    }

    [Fact]
    public void CreateKeepsValuesInOrderAndTrimmed()
    {
        var option = OptionDefinition.Create(TenantId, " Talla ", ["S", " M ", "L"], true, Now);

        Assert.Equal("Talla", option.Name);
        Assert.Equal(["S", "M", "L"], option.Values.Select(v => v.Value));
        Assert.Equal([0, 1, 2], option.Values.Select(v => v.Position));
    }

    [Fact]
    public void AddValueAppendsAtEnd()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S", "M"], true, Now);

        option.AddValue("L");

        Assert.Equal(["S", "M", "L"], option.Values.Select(v => v.Value));
        Assert.Equal(2, option.Values[2].Position);
    }

    [Fact]
    public void AddValueRejectsDuplicate()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S"], true, Now);

        var ex = Assert.Throws<CatalogDomainException>(() => option.AddValue(" s"));

        Assert.Equal("catalog.option.value_duplicate", ex.Code);
    }

    [Fact]
    public void AddValueRejectsEmpty()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S"], true, Now);

        var ex = Assert.Throws<CatalogDomainException>(() => option.AddValue("  "));

        Assert.Equal("catalog.option.value_required", ex.Code);
    }

    [Fact]
    public void CreateRejectsEmptyName()
    {
        var ex = Assert.Throws<CatalogDomainException>(() =>
            OptionDefinition.Create(TenantId, "  ", ["S"], true, Now));

        Assert.Equal("catalog.option.name_required", ex.Code);
    }

    [Fact]
    public void RenameValueRejectsCollisionWithAnotherValue()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S", "M"], true, Now);

        var ex = Assert.Throws<CatalogDomainException>(() =>
            option.RenameValue(option.Values[0].Id, "m"));

        Assert.Equal("catalog.option.value_duplicate", ex.Code);
    }

    [Fact]
    public void RenameValueAllowsChangingOnlyCase()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["s"], true, Now);

        option.RenameValue(option.Values[0].Id, "S");

        Assert.Equal("S", option.Values[0].Value);
    }

    [Fact]
    public void SetActiveAndSetValueActiveToggle()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S"], true, Now);

        option.SetActive(false);
        option.SetValueActive(option.Values[0].Id, false);

        Assert.False(option.IsActive);
        Assert.False(option.Values[0].IsActive);
    }

    [Fact]
    public void RenameValidatesName()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", [], true, Now);

        option.Rename(" Tamaño ");
        Assert.Equal("Tamaño", option.Name);

        var ex = Assert.Throws<CatalogDomainException>(() => option.Rename(""));
        Assert.Equal("catalog.option.name_required", ex.Code);
    }
}
