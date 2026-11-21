using FluentValidation;
using Modules.Coordinadora.Application.Settings;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary>El cuerpo opcional de cotizar y generar (spec 2026-11-21 §5.3): el paquete y el valor declarado de este envío.</summary>
public sealed record CoordinadoraShipmentOverridesInput(CoordinadoraPackageInput? Package, decimal? DeclaredValue);

/// <summary>Las reglas del paquete con su campo, y un valor declarado entero y positivo.</summary>
public sealed class CoordinadoraShipmentOverridesValidator : AbstractValidator<CoordinadoraShipmentOverridesInput>
{
    public CoordinadoraShipmentOverridesValidator()
    {
        When(input => input.Package is not null, () =>
            RuleFor(input => input.Package!).SetValidator(new CoordinadoraPackageInputValidator("package")));
        RuleFor(input => input.DeclaredValue)
            .GreaterThan(0)
            .Must(value => value is null || value == decimal.Truncate(value.Value))
            .WithMessage("'declaredValue' must be a whole amount in pesos.")
            .When(input => input.DeclaredValue is not null)
            .OverridePropertyName("declaredValue");
    }

    /// <summary>Lo que el armador recibe, o <c>null</c> si el cuerpo no vino.</summary>
    public static CoordinadoraShipmentOverrides? ToOverrides(CoordinadoraShipmentOverridesInput? input) =>
        input is null
            ? null
            : new CoordinadoraShipmentOverrides(CoordinadoraPackageInputValidator.ToPackage(input.Package), input.DeclaredValue);
}
