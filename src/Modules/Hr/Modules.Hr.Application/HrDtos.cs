using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Hr.Application;

// Los DTO de respuesta de empleados y contratos (plan 2026-11-20, «DTOs»). Enums como texto, por nombre; los opcionales viajan
// siempre, como null. Lo que la pantalla necesita saber (si está programado, si es el vigente, si hay aviso de vencimiento, si se puede
// eliminar) se deriva acá y nunca se calcula en el navegador: Backend for Frontend.

/// <summary>Mismo criterio que <c>LoanMemberDto</c>: la membresía con el nombre que tiene hoy.</summary>
public sealed record MemberRefDto(Guid MemberId, string DisplayName, bool IsActive);

public sealed record FileRefDto(Guid FileId, string FileName);

/// <summary>Con <c>Other</c> los tres campos del banco van en null.</summary>
public sealed record PaymentDto(string PaymentMethod, string? BankName, string? BankAccountType, string? BankAccountNumber);

/// <param name="Country">La nacionalidad (ISO 3166-1 alfa-2): siempre presente, nunca nula.</param>
/// <param name="Payment">Nulo sin <c>hr.contract.read</c> o si nunca se cargó.</param>
public sealed record EmployeeDto(
    Guid Id,
    string FirstName,
    string LastName,
    string DocumentType,
    string DocumentNumber,
    string Country,
    DateOnly? BirthDate,
    string? Phone,
    string? PersonalEmail,
    string? WorkEmail,
    string? Address,
    MunicipalityRefDto? City,
    MemberRefDto? Member,
    string? HealthProvider,
    string? PensionFund,
    string? SeveranceFund,
    string? OccupationalRiskProvider,
    string ContributorSubtype,
    bool ForeignPensionExempt,
    IReadOnlyList<string> RestDays,
    PaymentDto? Payment,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="BelowMinimumAcknowledged">Quedó bajo el SMMLV de su fecha con confirmación.</param>
/// <param name="Scheduled"><c>effectiveDate</c> posterior a hoy en el calendario del tenant.</param>
/// <param name="Current">Es el registro vigente hoy.</param>
public sealed record SalaryRecordDto(
    Guid Id,
    DateOnly EffectiveDate,
    decimal Amount,
    bool Teleworker,
    bool BelowMinimumAcknowledged,
    FileRefDto? Document,
    bool Scheduled,
    bool Current,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="CanEdit">
/// El contrato está activo y ninguna nómina <c>Approved</c> o <c>Closed</c> de la empresa del contrato, con resultado del empleado,
/// usó los días que rige el registro. Lo calcula el backend porque la pantalla no ve la nómina (Backend for Frontend). No mira
/// permisos, como el resto de la ficha. En <c>true</c> no garantiza cualquier cambio: mover la fecha puede alcanzar otra nómina.
/// </param>
/// <param name="CanDelete">
/// <c>CanEdit</c>, no es el primero y el historial sin él sigue siendo válido (vecinos distintos, cambio de lugar el día 1): la misma
/// regla que aplica el <c>DELETE</c>, para que la pantalla no ofrezca un botón que va a responder 422.
/// </param>
public sealed record AssignmentRecordDto(
    Guid Id,
    DateOnly EffectiveDate,
    SettingRefDto Position,
    SettingRefDto? Area,
    SettingRefDto Site,
    SettingRefDto Workplace,
    FileRefDto? Document,
    bool Scheduled,
    bool Current,
    DateTimeOffset CreatedAt,
    bool CanEdit,
    bool CanDelete);

/// <param name="IsShort">Menos de un año sobre el fin anterior: cuenta para la regla de las tres prórrogas.</param>
public sealed record RenewalDto(
    Guid Id,
    DateOnly PreviousEndDate,
    DateOnly NewEndDate,
    [property: JsonPropertyName("short")] bool IsShort,
    FileRefDto? Document,
    DateTimeOffset CreatedAt);

/// <summary>Lo vigente hoy: el último registro con <c>effectiveDate</c> ≤ hoy; si el contrato aún no empieza, el primero.</summary>
public sealed record ContractCurrentDto(
    decimal Salary,
    bool Teleworker,
    string SalaryType,
    SettingRefDto Position,
    SettingRefDto? Area,
    SettingRefDto Site,
    SettingRefDto Workplace);

/// <param name="JustCause"><c>true</c> en <c>DismissalWithJustCause</c>, <c>false</c> en <c>DismissalWithoutJustCause</c>, nulo en el resto.</param>
public sealed record TerminationDto(string Reason, bool? JustCause, DateOnly Date, string? Notes);

/// <param name="Company">La empresa empleadora, fija: siempre presente (<c>IsActive</c> dice si se desactivó después).</param>
/// <param name="EndDate">El fin vigente (el de la última prórroga); estimado en <c>WorkOrLabor</c>; nulo en <c>Indefinite</c>.</param>
/// <param name="EndedOn">El último día de un contrato terminado, convertido o cerrado.</param>
/// <param name="RenewalNoticeDue">Término fijo vigente que vence en 30 días o menos (o ya venció).</param>
/// <param name="DaysUntilEnd">Fin menos hoy, negativo si ya pasó; nulo sin fin o si no está vigente.</param>
public sealed record ContractDto(
    Guid Id,
    CompanyRefDto Company,
    string ContractType,
    string Duration,
    string SalaryType,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    DateOnly? EndedOn,
    DateOnly? ConvertedToIndefiniteOn,
    Guid? PreviousContractId,
    FileRefDto? Document,
    TerminationDto? Termination,
    bool Started,
    ContractCurrentDto Current,
    bool RenewalNoticeDue,
    int? DaysUntilEnd,
    IReadOnlyList<SalaryRecordDto> Salaries,
    IReadOnlyList<AssignmentRecordDto> Assignments,
    IReadOnlyList<RenewalDto> Renewals);

/// <summary>
/// La respuesta de leer un empleado y de toda escritura sobre él: la pantalla se repinta de la respuesta (Backend for Frontend), sin
/// volver a pedir el detalle. Sin <c>hr.contract.read</c>, <c>CanReadContract</c> es <c>false</c>, el pago y el contrato van en null
/// y los contratos anteriores vacíos, también en la respuesta de una escritura.
/// </summary>
public sealed record EmployeeDetailDto(
    EmployeeDto Employee,
    bool CanReadContract,
    bool CanDelete,
    ContractDto? Contract,
    IReadOnlyList<ContractDto> PreviousContracts);

/// <summary>Los siete últimos salen del contrato vigente: nulos sin contrato vigente o sin <c>hr.contract.read</c>.</summary>
public sealed record EmployeeListItemDto(
    Guid Id,
    string FirstName,
    string LastName,
    string DocumentType,
    string DocumentNumber,
    string Status,
    string? Company,
    string? Position,
    string? Site,
    string? ContractType,
    string? Duration,
    DateOnly? EndDate,
    bool? RenewalNoticeDue);

/// <param name="ExpiringSoon">Nulo sin <c>hr.contract.read</c>.</param>
public sealed record EmployeeListSummaryDto(int Active, int Retired, int Deactivated, int? ExpiringSoon);

public sealed record EmployeePageDto(
    IReadOnlyList<EmployeeListItemDto> Items, int Total, int Page, int PageSize, EmployeeListSummaryDto Summary);

/// <summary>Escalares: montos como número, fechas <c>yyyy-MM-dd</c>, enums por nombre, referencias por el nombre que tenían al escribirse.</summary>
public sealed record ActivityChangeDto(string Field, JsonElement? Before, JsonElement? After);

public sealed record ActivityActorDto(Guid SubjectId, string DisplayName);

public sealed record ActivityEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    ActivityActorDto Actor,
    string Action,
    string Category,
    Guid? ContractId,
    IReadOnlyList<ActivityChangeDto> Changes);

public sealed record ActivityPageDto(IReadOnlyList<ActivityEntryDto> Items, int Total, int Page, int PageSize);
