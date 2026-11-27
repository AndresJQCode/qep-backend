namespace Modules.Hr.Application;

// Todo anulable: un campo ausente llega al validador (422 con el campo) y no al binding (400). Los enums viajan como texto, por
// nombre exacto; los valida HrEnums.TryParse.

/// <summary>El cuerpo de <c>PUT /hr/employees/{id}</c> (reemplaza todo) y la base del alta.</summary>
public record EmployeeRequest
{
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? DocumentType { get; init; }

    public string? DocumentNumber { get; init; }

    /// <summary>Nacionalidad, ISO 3166-1 alfa-2. Nulo ⇒ el país del tenant.</summary>
    public string? Country { get; init; }

    public DateOnly? BirthDate { get; init; }

    public string? Phone { get; init; }

    public string? PersonalEmail { get; init; }

    public string? WorkEmail { get; init; }

    public string? Address { get; init; }

    public Guid? CityId { get; init; }

    public Guid? MemberId { get; init; }

    public string? HealthProvider { get; init; }

    public string? PensionFund { get; init; }

    public string? SeveranceFund { get; init; }

    public string? OccupationalRiskProvider { get; init; }

    /// <summary>Nulo ⇒ <c>Standard</c>.</summary>
    public string? ContributorSubtype { get; init; }

    /// <summary>Nulo ⇒ <c>false</c>.</summary>
    public bool? ForeignPensionExempt { get; init; }

    /// <summary>Nulo ⇒ <c>["Sunday"]</c>; si viene, de 1 a 6 días distintos.</summary>
    public IReadOnlyList<string>? RestDays { get; init; }
}

public sealed record CreateEmployeeRequest : EmployeeRequest
{
    public PaymentRequest? Payment { get; init; }

    public NewContractRequest? Contract { get; init; }
}

/// <summary>Los cuatro nulos limpian el pago.</summary>
public sealed record PaymentRequest(string? PaymentMethod, string? BankName, string? BankAccountType, string? BankAccountNumber);

public sealed record ContractTermsRequest(
    Guid? CompanyId,
    string? ContractType,
    string? Duration,
    string? SalaryType,
    DateOnly? StartDate,
    DateOnly? EndDate,
    Guid? DocumentFileId);

/// <summary>Rige desde <c>startDate</c>.</summary>
public sealed record InitialSalaryRequest(decimal? Amount, bool? Teleworker, bool? AcknowledgeBelowMinimum);

/// <summary>Rige desde <c>startDate</c>; la sede sale del centro de trabajo.</summary>
public sealed record InitialAssignmentRequest(Guid? PositionId, Guid? AreaId, Guid? WorkplaceId);

/// <summary>El alta (dentro de <see cref="CreateEmployeeRequest"/>), la reincorporación y la conversión de tipo.</summary>
public sealed record NewContractRequest(
    ContractTermsRequest? Contract,
    InitialSalaryRequest? Salary,
    InitialAssignmentRequest? Assignment);

public sealed record DeactivateRequest(DateOnly? Date);

/// <summary>Un salario nuevo: rige desde <c>effectiveDate</c>, después del último registro y no después del fin del contrato.</summary>
public sealed record SalaryRequest(
    DateOnly? EffectiveDate, decimal? Amount, bool? Teleworker, bool? AcknowledgeBelowMinimum, Guid? DocumentFileId);

/// <summary>Reescribe un registro: conserva su fecha. <c>ConfirmRewrite</c> distinto de <c>true</c> es 422 <c>hr.salary.rewrite_unconfirmed</c>.</summary>
public sealed record SalaryRewriteRequest(decimal? Amount, bool? Teleworker, bool? AcknowledgeBelowMinimum, bool? ConfirmRewrite);

/// <summary>Un cargo y lugar nuevo; la sede sale del centro de trabajo.</summary>
public sealed record AssignmentRequest(
    DateOnly? EffectiveDate, Guid? PositionId, Guid? AreaId, Guid? WorkplaceId, Guid? DocumentFileId);

/// <summary>
/// Reescribe un registro de cargo y lugar (spec 2026-11-26): fecha, cargo, área y centro; la sede sale del centro. El PDF se sigue
/// cambiando por su propia ruta. <c>ConfirmRewrite</c> distinto de <c>true</c> es 422 <c>hr.assignment.rewrite_unconfirmed</c>.
/// </summary>
public sealed record AssignmentRewriteRequest(
    DateOnly? EffectiveDate, Guid? PositionId, Guid? AreaId, Guid? WorkplaceId, bool? ConfirmRewrite);

public sealed record RenewalRequest(DateOnly? NewEndDate, Guid? DocumentFileId);

public sealed record TerminateRequest(string? Reason, DateOnly? Date, string? Notes);

/// <summary>El PDF de un contrato o de un registro; <c>null</c> lo quita.</summary>
public sealed record DocumentRequest(Guid? FileId);
