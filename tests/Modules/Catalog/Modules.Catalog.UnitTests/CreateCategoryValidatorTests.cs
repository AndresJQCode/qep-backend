using FluentValidation.TestHelper;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Categorías: el dominio ya rechaza estos casos con un código; el validador existe para que el 422
/// lleve el mapa <c>errors</c> por campo y el formulario marque el input culpable.
/// </summary>
public sealed class CreateCategoryValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly string Eighty = new('a', 80);

    [Fact]
    public void AcceptsAValidName()
    {
        var validator = new CreateCategoryValidator();

        validator.TestValidate(new CreateCategoryCommand(TenantId, "Bebidas", null))
            .ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new CreateCategoryCommand(TenantId, "Bebidas", Guid.CreateVersion7()))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void RejectsEmptyName()
    {
        var result = new CreateCategoryValidator().TestValidate(
            new CreateCategoryCommand(TenantId, "  ", null));

        result.ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.category.name_required");
    }

    [Fact]
    public void MeasuresTheNameAfterTrimming()
    {
        var validator = new CreateCategoryValidator();

        validator.TestValidate(new CreateCategoryCommand(TenantId, $" {Eighty} ", null))
            .ShouldNotHaveValidationErrorFor(command => command.Name);
        validator.TestValidate(new CreateCategoryCommand(TenantId, Eighty + "a", null))
            .ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.category.name_too_long");
    }
}
