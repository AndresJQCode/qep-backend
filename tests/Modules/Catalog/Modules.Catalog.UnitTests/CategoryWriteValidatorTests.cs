using FluentValidation.TestHelper;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// The rename keeps the create's name rules and codes, so the form reads the same 422 in both. The
/// quick-access validator only checks the shape: duplicates and unknown ids are domain rules.
/// </summary>
public sealed class CategoryWriteValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();
    private static readonly string Eighty = new('a', 80);

    private static UpdateCategoryCommand Update(string name, Guid? parentId = null) =>
        new(TenantId, CategoryId, name, parentId, ExpectedVersion: 1);

    [Fact]
    public void UpdateAcceptsAValidNameWithOrWithoutParent()
    {
        var validator = new UpdateCategoryValidator();

        validator.TestValidate(Update("Bebidas")).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(Update("Bebidas", Guid.CreateVersion7())).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateRejectsABlankName()
    {
        new UpdateCategoryValidator().TestValidate(Update("  "))
            .ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.category.name_required");
    }

    [Fact]
    public void UpdateMeasuresTheNameAfterTrimming()
    {
        var validator = new UpdateCategoryValidator();

        validator.TestValidate(Update($" {Eighty} "))
            .ShouldNotHaveValidationErrorFor(command => command.Name);
        validator.TestValidate(Update(Eighty + "a"))
            .ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.category.name_too_long");
    }

    [Fact]
    public void QuickAccessAcceptsAnEmptyOrFilledList()
    {
        var validator = new ReplacePosQuickAccessValidator();

        validator.TestValidate(new ReplacePosQuickAccessCommand(TenantId, []))
            .ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new ReplacePosQuickAccessCommand(TenantId, [CategoryId]))
            .ShouldNotHaveAnyValidationErrors();
    }

    // A body without categoryIds must not read as "unpin everything".
    [Fact]
    public void QuickAccessRejectsAMissingList()
    {
        new ReplacePosQuickAccessValidator().TestValidate(new ReplacePosQuickAccessCommand(TenantId, null))
            .ShouldHaveValidationErrorFor(command => command.CategoryIds);
    }
}
