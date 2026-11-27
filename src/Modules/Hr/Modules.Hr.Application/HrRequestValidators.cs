using System.Text.RegularExpressions;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

internal static class HrRequestRules
{
    public const decimal MaxMoney = 10_000_000_000_000_000m;

    /// <summary>Dinero: mayor que cero, hasta dos decimales y por debajo de 10^16 (<c>numeric(18,2)</c>).</summary>
    public static IRuleBuilderOptions<T, decimal?> MustBeMoney<T>(this IRuleBuilder<T, decimal?> rule) => rule
        .Must(amount => amount is > 0).WithMessage("Escribe un monto mayor que cero.")
        .Must(amount => amount is null || (decimal.Round(amount.Value, 2) == amount.Value && amount.Value < MaxMoney))
        .WithMessage("El monto admite hasta dos decimales y debe ser menor que 10.000.000.000.000.000.");

    public static IRuleBuilderOptions<T, string?> MustBeAnEnum<T, TEnum>(this IRuleBuilder<T, string?> rule, string message)
        where TEnum : struct, Enum => rule
        .Must(value => HrEnums.TryParse<TEnum>(value, out _)).WithMessage(message);

    public static IRuleBuilderOptions<T, Guid?> MustBeAnId<T>(this IRuleBuilder<T, Guid?> rule, string message) => rule
        .Must(id => id is { } value && value != Guid.Empty).WithMessage(message);

    public static IRuleBuilderOptions<T, string?> MaxTrimmed<T>(this IRuleBuilder<T, string?> rule, int max, string label) => rule
        .Must(text => text is null || text.Trim().Length <= max).WithMessage($"{label} no puede pasar de {max} caracteres.");
}

// Los validadores son públicos a propósito: AddValidatorsFromAssembly sólo registra tipos públicos, y uno que no se registra deja
// pasar el cuerpo sin validar. El cruce país–documento y el exento no van acá: dependen del país efectivo, que puede salir del
// tenant, y los aplica el caso de uso con EmployeeCountry.Ensure.
public sealed partial class EmployeeRequestValidator : HrValidator<EmployeeRequest>
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    [GeneratedRegex(@"^[A-Za-z]{2}$")]
    private static partial Regex CountryShape();

    public EmployeeRequestValidator()
    {
        RuleFor(request => request.FirstName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe los nombres.")
            .MaxTrimmed(100, "Los nombres");
        RuleFor(request => request.LastName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe los apellidos.")
            .MaxTrimmed(100, "Los apellidos");
        RuleFor(request => request.DocumentType)
            .MustBeAnEnum<EmployeeRequest, HrDocumentType>("Elige un tipo de documento válido.");
        RuleFor(request => request.DocumentNumber)
            .Must(number => !string.IsNullOrWhiteSpace(number)).WithMessage("Escribe el número de documento.")
            .Must(number => number is null || number.Trim().Length <= 30).WithMessage("El número de documento no puede pasar de 30 caracteres.")
            .Must(number => number is null || !number.Trim().Any(char.IsWhiteSpace))
            .WithMessage("El número de documento no puede tener espacios.");
        // Con el tipo ya válido y el número escrito; los espacios ya tienen su propio mensaje. Bloquea también un número
        // viejo reenviado sin cambios.
        RuleFor(request => request.DocumentNumber)
            .Must((request, number) => !HrEnums.TryParse<HrDocumentType>(request.DocumentType, out var type)
                || HrDocumentNumberRules.IsValidFor(type, number))
            .WithMessage(HrDocumentNumberRules.NumericTypesMessage)
            .When(request => !string.IsNullOrWhiteSpace(request.DocumentNumber)
                && !request.DocumentNumber.Trim().Any(char.IsWhiteSpace));
        RuleFor(request => request.Country)
            .Must(country => country is null || CountryShape().IsMatch(country.Trim()))
            .WithMessage("El país es un código de dos letras, por ejemplo CO.");
        RuleFor(request => request.Phone)
            .Must(phone => string.IsNullOrWhiteSpace(phone) || E164().IsMatch(phone.Trim()))
            .WithMessage("El teléfono va en formato internacional, por ejemplo +573001234567.");
        RuleFor(request => request.PersonalEmail).MustBeAnEmail();
        RuleFor(request => request.WorkEmail).MustBeAnEmail();
        RuleFor(request => request.Address).MaxTrimmed(200, "La dirección");
        RuleFor(request => request.HealthProvider).MaxTrimmed(120, "La EPS");
        RuleFor(request => request.PensionFund).MaxTrimmed(120, "El fondo de pensiones");
        RuleFor(request => request.SeveranceFund).MaxTrimmed(120, "El fondo de cesantías");
        RuleFor(request => request.OccupationalRiskProvider).MaxTrimmed(120, "La ARL");
        RuleFor(request => request.ContributorSubtype)
            .Must(subtype => subtype is null || HrEnums.TryParse<ContributorSubtype>(subtype, out _))
            .WithMessage("Elige un subtipo de cotizante válido.");
        RuleFor(request => request.RestDays)
            .Must(days => days is null || (days.Count is >= 1 and <= 6
                && days.All(day => HrEnums.TryParse<DayOfWeek>(day, out _))
                && days.Distinct(StringComparer.Ordinal).Count() == days.Count))
            .WithMessage("Elige de uno a seis días de descanso distintos.");
    }
}

internal static class EmailRules
{
    public static IRuleBuilderOptions<T, string?> MustBeAnEmail<T>(this IRuleBuilder<T, string?> rule) => rule
        .Must(email => string.IsNullOrWhiteSpace(email) || new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email.Trim()))
        .WithMessage("El correo no es válido.")
        .Must(email => email is null || email.Trim().Length <= 254).WithMessage("El correo no puede pasar de 254 caracteres.");
}

public sealed partial class PaymentRequestValidator : HrValidator<PaymentRequest>
{
    [GeneratedRegex(@"^[0-9]{1,17}$")]
    private static partial Regex AccountNumber();

    public PaymentRequestValidator()
    {
        RuleFor(request => request.PaymentMethod)
            .Must(method => method is null || HrEnums.TryParse<PaymentMethod>(method, out _))
            .WithMessage("Elige un método de pago válido.");

        When(request => request.PaymentMethod is null, () =>
        {
            RuleFor(request => request.PaymentMethod)
                .Must((request, _) => request.BankName is null && request.BankAccountType is null && request.BankAccountNumber is null)
                .WithMessage("Elige el método de pago.");
        });

        When(request => request.PaymentMethod == nameof(PaymentMethod.BankTransfer), () =>
        {
            RuleFor(request => request.BankName)
                .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe el banco.")
                .Must(name => name is null || name.Trim().Length <= 80).WithMessage("El banco no puede pasar de 80 caracteres.");
            RuleFor(request => request.BankAccountType)
                .Must(type => HrEnums.TryParse<BankAccountType>(type, out _)).WithMessage("Elige el tipo de cuenta.");
            RuleFor(request => request.BankAccountNumber)
                .Must(number => number is not null && AccountNumber().IsMatch(number.Trim()))
                .WithMessage("El número de cuenta tiene de 1 a 17 dígitos.");
        });

        When(request => request.PaymentMethod == nameof(PaymentMethod.Other), () =>
        {
            RuleFor(request => request.BankName).Null().WithMessage("Con «Otro» no se escribe banco.");
            RuleFor(request => request.BankAccountType).Null().WithMessage("Con «Otro» no se elige tipo de cuenta.");
            RuleFor(request => request.BankAccountNumber).Null().WithMessage("Con «Otro» no se escribe número de cuenta.");
        });
    }
}

public sealed class ContractTermsRequestValidator : HrValidator<ContractTermsRequest>
{
    public ContractTermsRequestValidator()
    {
        RuleFor(request => request.CompanyId).MustBeAnId("Elige la empresa del contrato.");
        RuleFor(request => request.ContractType).MustBeAnEnum<ContractTermsRequest, ContractType>("Elige un tipo de contrato válido.");
        RuleFor(request => request.Duration).MustBeAnEnum<ContractTermsRequest, ContractDuration>("Elige una duración válida.");
        RuleFor(request => request.SalaryType).MustBeAnEnum<ContractTermsRequest, SalaryType>("Elige un tipo de salario válido.");
        RuleFor(request => request.StartDate).NotNull().WithMessage("Escribe la fecha de inicio.");

        When(request => request.Duration is nameof(ContractDuration.FixedTerm) or nameof(ContractDuration.WorkOrLabor), () =>
        {
            RuleFor(request => request.EndDate).NotNull().WithMessage("Escribe la fecha de terminación.");
        });
        When(request => request.Duration == nameof(ContractDuration.Indefinite), () =>
        {
            RuleFor(request => request.EndDate).Null().WithMessage("Un contrato a término indefinido no tiene fecha de terminación.");
        });
        RuleFor(request => request.EndDate)
            .Must((request, end) => end is null || request.StartDate is null || end >= request.StartDate)
            .WithMessage("La terminación no puede ser anterior al inicio.");

        // El salario integral es sólo del empleado: un contratista o un aprendiz no lo tienen.
        RuleFor(request => request.SalaryType)
            .Must((request, salaryType) => salaryType != nameof(SalaryType.Integral) || request.ContractType == nameof(ContractType.Employee))
            .WithMessage("El salario integral es sólo para empleados.");
    }
}

public sealed class InitialSalaryRequestValidator : HrValidator<InitialSalaryRequest>
{
    public InitialSalaryRequestValidator() => RuleFor(request => request.Amount).MustBeMoney();
}

public sealed class InitialAssignmentRequestValidator : HrValidator<InitialAssignmentRequest>
{
    public InitialAssignmentRequestValidator()
    {
        RuleFor(request => request.PositionId).MustBeAnId("Elige un cargo.");
        RuleFor(request => request.WorkplaceId).MustBeAnId("Elige un centro de trabajo.");
    }
}

public sealed class NewContractRequestValidator : HrValidator<NewContractRequest>
{
    public NewContractRequestValidator()
    {
        RuleFor(request => request.Contract).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Faltan las condiciones del contrato.")
            .SetValidator(new ContractTermsRequestValidator()!);
        RuleFor(request => request.Salary).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Falta el salario inicial.")
            .SetValidator(new InitialSalaryRequestValidator()!);
        RuleFor(request => request.Assignment).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Falta el cargo y el lugar de trabajo.")
            .SetValidator(new InitialAssignmentRequestValidator()!);
    }
}

public sealed class CreateEmployeeRequestValidator : HrValidator<CreateEmployeeRequest>
{
    public CreateEmployeeRequestValidator()
    {
        Include(new EmployeeRequestValidator());
        RuleFor(request => request.Payment!).SetValidator(new PaymentRequestValidator()).When(request => request.Payment is not null);
        RuleFor(request => request.Contract).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Faltan los datos del contrato.")
            .SetValidator(new NewContractRequestValidator()!);
    }
}

public sealed class DeactivateRequestValidator : HrValidator<DeactivateRequest>
{
    public DeactivateRequestValidator() =>
        RuleFor(request => request.Date).NotNull().WithMessage("Escribe la fecha de desactivación.");
}

public sealed class SalaryRequestValidator : HrValidator<SalaryRequest>
{
    public SalaryRequestValidator()
    {
        RuleFor(request => request.EffectiveDate).NotNull().WithMessage("Escribe la fecha desde la que rige.");
        RuleFor(request => request.Amount).MustBeMoney();
    }
}

// `confirmRewrite` no se valida acá: su ausencia es el código de dominio hr.salary.rewrite_unconfirmed, para que el frontend abra la
// confirmación en vez de marcar un campo.
public sealed class SalaryRewriteRequestValidator : HrValidator<SalaryRewriteRequest>
{
    public SalaryRewriteRequestValidator() => RuleFor(request => request.Amount).MustBeMoney();
}

public sealed class AssignmentRequestValidator : HrValidator<AssignmentRequest>
{
    public AssignmentRequestValidator()
    {
        RuleFor(request => request.EffectiveDate).NotNull().WithMessage("Escribe la fecha desde la que rige.");
        RuleFor(request => request.PositionId).MustBeAnId("Elige un cargo.");
        RuleFor(request => request.WorkplaceId).MustBeAnId("Elige un centro de trabajo.");
    }
}

public sealed class RenewalRequestValidator : HrValidator<RenewalRequest>
{
    public RenewalRequestValidator() =>
        RuleFor(request => request.NewEndDate).NotNull().WithMessage("Escribe la nueva fecha de terminación.");
}

public sealed class TerminateRequestValidator : HrValidator<TerminateRequest>
{
    public TerminateRequestValidator()
    {
        RuleFor(request => request.Reason)
            .MustBeAnEnum<TerminateRequest, TerminationReason>("Elige un motivo de terminación válido.");
        RuleFor(request => request.Date).NotNull().WithMessage("Escribe la fecha de terminación.");
        RuleFor(request => request.Notes).MaxTrimmed(1000, "Las notas");
    }
}
