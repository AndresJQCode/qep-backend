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

    // Tarea 4: el PUT de la biblioteca reemplaza la lista de valores entera y en orden.
    [Fact]
    public void ReplaceValuesKeepsIdsReordersRenamesAddsAndRemoves()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S", "M", "L"], true, Now);
        var small = option.Values[0].Id;
        var large = option.Values[2].Id;

        option.ReplaceValues(
        [
            new OptionValueInput(large, " XL ", false),
            new OptionValueInput(small, "S", true),
            new OptionValueInput(null, "XXL", true),
        ]);

        Assert.Equal(["XL", "S", "XXL"], option.Values.Select(v => v.Value));
        Assert.Equal([0, 1, 2], option.Values.Select(v => v.Position));
        Assert.Equal(large, option.Values[0].Id);
        Assert.False(option.Values[0].IsActive);
        Assert.Equal(small, option.Values[1].Id);
        Assert.True(option.Values[2].IsActive);
    }

    [Fact]
    public void ReplaceValuesAllowsSwappingTwoNames()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S", "M"], true, Now);

        option.ReplaceValues(
        [
            new OptionValueInput(option.Values[0].Id, "M", true),
            new OptionValueInput(option.Values[1].Id, "S", true),
        ]);

        Assert.Equal(["M", "S"], option.Values.Select(v => v.Value));
    }

    [Fact]
    public void ReplaceValuesRejectsDuplicateInFinalList()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S"], true, Now);

        var ex = Assert.Throws<CatalogDomainException>(() => option.ReplaceValues(
        [
            new OptionValueInput(option.Values[0].Id, "S", true),
            new OptionValueInput(null, " s ", true),
        ]));

        Assert.Equal("catalog.option.value_duplicate", ex.Code);
    }

    [Fact]
    public void ReplaceValuesRejectsUnknownId()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S"], true, Now);

        var ex = Assert.Throws<CatalogDomainException>(() => option.ReplaceValues(
            [new OptionValueInput(Guid.CreateVersion7(), "S", true)]));

        Assert.Equal("catalog.option.value_not_found", ex.Code);
    }

    [Fact]
    public void RemovedValueIdsListsWhatTheBodyLeavesOut()
    {
        var option = OptionDefinition.Create(TenantId, "Talla", ["S", "M"], true, Now);

        var removed = option.RemovedValueIds([new OptionValueInput(option.Values[1].Id, "M", true)]);

        Assert.Equal([option.Values[0].Id], removed);
    }

    [Fact]
    public void NameAndValueAtSixtyCharactersPassAndSixtyOneFail()
    {
        var sixty = new string('a', 60);
        var option = OptionDefinition.Create(TenantId, sixty, [sixty], true, Now);
        Assert.Equal(sixty, option.Name);

        var name = Assert.Throws<CatalogDomainException>(() => option.Rename(sixty + "a"));
        Assert.Equal("catalog.option.name_too_long", name.Code);
        var value = Assert.Throws<CatalogDomainException>(() => option.AddValue(sixty + "b"));
        Assert.Equal("catalog.option.value_too_long", value.Code);
    }
}
