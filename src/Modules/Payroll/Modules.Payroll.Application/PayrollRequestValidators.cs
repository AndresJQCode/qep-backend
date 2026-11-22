using FluentValidation;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

// Los validadores son públicos a propósito: AddValidatorsFromAssembly sólo registra tipos públicos, y uno que no se registra deja
// pasar el cuerpo sin validar.
public sealed class PayrollSettingsRequestValidator : PayrollValidator<PayrollSettingsRequest>
{
    public PayrollSettingsRequestValidator()
    {
        RuleFor(request => request.TransportFrequency)
            .Must(value => EnumNames.TryParse<TransportFrequency>(value, out _))
            .WithMessage("Elige una frecuencia válida (Biweekly o Monthly).");
        RuleFor(request => request.TransportCapBase)
            .Must(value => EnumNames.TryParse<TransportCapBase>(value, out _))
            .WithMessage("Elige una base válida para el tope del auxilio de transporte.");
        RuleFor(request => request.OvertimeBase)
            .Must(value => EnumNames.TryParse<OvertimeBase>(value, out _))
            .WithMessage("Elige una base válida para las horas extras.");
        RuleFor(request => request.Exonerated).NotNull().WithMessage("Indica si la empresa está exonerada.");
        RuleFor(request => request.DualControl).NotNull().WithMessage("Indica si usa doble control.");
    }
}

public sealed class ConceptRequestValidator : PayrollValidator<ConceptRequest>
{
    public const int NameMaxLength = 80;

    public const decimal MultiplierMax = 10m;

    public const int MultiplierDecimals = 4;

    public ConceptRequestValidator()
    {
        RuleFor(request => request.Kind)
            .Must(value => EnumNames.TryParse<ConceptKind>(value, out _))
            .WithMessage("Elige un tipo válido (Overtime, Income o Deduction).");
        RuleFor(request => request.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe un nombre.")
            .Must(name => name is null || name.Trim().Length <= NameMaxLength)
            .WithMessage($"El nombre no puede pasar de {NameMaxLength} caracteres.");

        // Sólo las horas extras llevan multiplicador, y es obligatorio.
        RuleFor(request => request.Multiplier)
            .Must(value => value is { } multiplier && multiplier > 0 && multiplier <= MultiplierMax && HasAtMostFourDecimals(multiplier))
            .WithMessage($"El multiplicador debe ser mayor que 0 y hasta {MultiplierMax:0}, con máximo {MultiplierDecimals} decimales.")
            .When(request => IsKind(request, ConceptKind.Overtime));
        RuleFor(request => request.Multiplier)
            .Null().WithMessage("Sólo las horas extras llevan multiplicador.")
            .When(request => IsKnownKind(request) && !IsKind(request, ConceptKind.Overtime));

        // La clasificación sugerida es de los ingresos; un ingreso puede no sugerir nada (nulo).
        RuleFor(request => request.DefaultSalaryConstitutive)
            .Null().WithMessage("Sólo los ingresos llevan una clasificación sugerida.")
            .When(request => IsKnownKind(request) && !IsKind(request, ConceptKind.Income));
    }

    private static bool IsKnownKind(ConceptRequest request) => EnumNames.TryParse<ConceptKind>(request.Kind, out _);

    private static bool IsKind(ConceptRequest request, ConceptKind kind) =>
        EnumNames.TryParse<ConceptKind>(request.Kind, out var parsed) && parsed == kind;

    private static bool HasAtMostFourDecimals(decimal value) => decimal.Round(value, MultiplierDecimals) == value;
}
