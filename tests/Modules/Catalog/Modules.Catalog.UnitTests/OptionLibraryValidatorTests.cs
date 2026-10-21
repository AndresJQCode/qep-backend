using FluentValidation.TestHelper;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Biblioteca de opciones: el dominio ya rechaza estos casos con un código; el validador existe para que el 422
/// lleve el mapa <c>errors</c> por campo y el formulario marque el input culpable. Cada regla
/// lleva como código de error el mismo código del dominio.
/// </summary>
public sealed class OptionLibraryValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly string Sixty = new('a', 60);

    [Fact]
    public void CreateAcceptsAValidOption()
    {
        var result = new CreateOptionValidator().TestValidate(
            new CreateOptionCommand(TenantId, "Material", ["Algodón", "Lino"]));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CreateRejectsEmptyName()
    {
        var result = new CreateOptionValidator().TestValidate(
            new CreateOptionCommand(TenantId, "  ", []));

        result.ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.option.name_required");
    }

    [Fact]
    public void CreateMeasuresTheNameAfterTrimming()
    {
        var validator = new CreateOptionValidator();

        validator.TestValidate(new CreateOptionCommand(TenantId, $" {Sixty} ", []))
            .ShouldNotHaveValidationErrorFor(command => command.Name);
        validator.TestValidate(new CreateOptionCommand(TenantId, Sixty + "a", []))
            .ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.option.name_too_long");
    }

    [Fact]
    public void CreateRejectsEmptyAndLongValuesByIndex()
    {
        var result = new CreateOptionValidator().TestValidate(
            new CreateOptionCommand(TenantId, "Material", ["Algodón", " ", Sixty + "a"]));

        result.ShouldHaveValidationErrorFor("Values[1]")
            .WithErrorCode("catalog.option.value_required");
        result.ShouldHaveValidationErrorFor("Values[2]")
            .WithErrorCode("catalog.option.value_too_long");
    }

    [Fact]
    public void CreateRejectsDuplicateValuesIgnoringCaseAndEdgeSpaces()
    {
        var result = new CreateOptionValidator().TestValidate(
            new CreateOptionCommand(TenantId, "Talla", ["M", " m "]));

        result.ShouldHaveValidationErrorFor("Values[1]")
            .WithErrorCode("catalog.option.value_duplicate");
    }

    [Fact]
    public void UpdateRejectsEmptyNameAndBadValues()
    {
        var result = new UpdateOptionValidator().TestValidate(new UpdateOptionCommand(
            TenantId,
            Guid.CreateVersion7(),
            "",
            true,
            [
                new OptionValueRequest(null, "S", true),
                new OptionValueRequest(null, "s", true),
                new OptionValueRequest(null, "", true),
                new OptionValueRequest(null, Sixty + "a", true),
            ]));

        result.ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorCode("catalog.option.name_required");
        result.ShouldHaveValidationErrorFor("Values[1].Name")
            .WithErrorCode("catalog.option.value_duplicate");
        result.ShouldHaveValidationErrorFor("Values[2].Name")
            .WithErrorCode("catalog.option.value_required");
        result.ShouldHaveValidationErrorFor("Values[3].Name")
            .WithErrorCode("catalog.option.value_too_long");
    }

    // Con bool, omitir isActive llegaba como false y desactivaba en silencio. Ahora el validador lo
    // exige en la opción y en cada valor, y marca la posición del valor que no lo trae.
    [Fact]
    public void UpdateRequiresIsActiveOnTheOptionAndOnEachValue()
    {
        var result = new UpdateOptionValidator().TestValidate(new UpdateOptionCommand(
            TenantId,
            Guid.CreateVersion7(),
            "Talla",
            null,
            [new OptionValueRequest(null, "S", true), new OptionValueRequest(null, "M", null)]));

        result.ShouldHaveValidationErrorFor(command => command.IsActive)
            .WithErrorCode("catalog.option.active_required");
        result.ShouldHaveValidationErrorFor("Values[1].IsActive")
            .WithErrorCode("catalog.option.value_active_required");
        result.ShouldNotHaveValidationErrorFor("Values[0].IsActive");
    }

    [Fact]
    public void UpdateAcceptsAValidBody()
    {
        var result = new UpdateOptionValidator().TestValidate(new UpdateOptionCommand(
            TenantId,
            Guid.CreateVersion7(),
            "Talla",
            false,
            [new OptionValueRequest(Guid.CreateVersion7(), "S", false), new OptionValueRequest(null, "M", true)]));

        result.ShouldNotHaveAnyValidationErrors();
    }
}
