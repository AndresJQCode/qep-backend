namespace Modules.Payroll.Application;

// Contrato HTTP de las vacaciones disfrutadas (plan, «Contrato HTTP»). Los enums viajan con su nombre y los opcionales siempre
// presentes como null. Todo lo que la pantalla pinta de las fechas lo calcula el backend: la pantalla nunca cuenta días.

/// <summary>
/// <c>POST</c> y <c>PUT</c> de vacaciones. Todo es anulable para que el validador devuelva el campo y no un 400 de binding.
/// <c>BusinessDays</c> y <c>ExternalMonths</c> son sólo de <c>External</c>: con <c>Qep</c> los calcula el backend.
/// </summary>
public sealed record VacationRequest(
    string? Origin,
    DateOnly? StartDate,
    DateOnly? EndDate,
    decimal? PreviousMonthIbc,
    int? BusinessDays,
    IReadOnlyList<ExternalVacationMonthRequest>? ExternalMonths);

/// <param name="Month"><c>yyyy-MM</c>.</param>
public sealed record ExternalVacationMonthRequest(
    string? Month, decimal? VacationPay, decimal? HealthAdvance, decimal? PensionAdvance, decimal? SolidarityFundAdvance);

/// <summary><c>POST …/vacations/preview</c>: lo mínimo para contar los días, sin escribir.</summary>
public sealed record VacationPreviewRequest(string? Origin, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>Los días de nómina de las vacaciones dentro de una quincena (<c>VacationCalendar</c>): la pantalla no los cuenta.</summary>
public sealed record VacationQuincenaDto(DateOnly StartDate, DateOnly EndDate, int Days);

public sealed record QuincenaRefDto(DateOnly StartDate, DateOnly EndDate);

/// <param name="PeriodId">Nulo mientras la quincena que paga todavía no existe.</param>
/// <param name="Status">El estado de esa quincena; nulo si no existe.</param>
public sealed record VacationPaidInDto(DateOnly StartDate, DateOnly EndDate, Guid? PeriodId, string? Status);

/// <param name="Month"><c>yyyy-MM</c>.</param>
/// <param name="VacationPay">Nulo si no se tecleó: el valor del día sale del salario.</param>
public sealed record ExternalVacationMonthDto(
    string Month, decimal? VacationPay, decimal HealthAdvance, decimal PensionAdvance, decimal SolidarityFundAdvance);

/// <summary>Falso si alguna quincena que las vacaciones alcanzan está Aprobada o Cerrada: la pantalla no ofrece lo que el backend rechazaría.</summary>
public sealed record VacationCanDto(bool Update, bool Delete);

/// <param name="Quincenas">Los días de nómina por quincena que tocan.</param>
/// <param name="PaidIn">La quincena que las paga; nulo con <c>External</c>.</param>
/// <param name="ExternalMonths">Vacío con <c>Qep</c>.</param>
public sealed record VacationDto(
    Guid Id,
    Guid EmployeeId,
    string Origin,
    DateOnly StartDate,
    DateOnly EndDate,
    int BusinessDays,
    DateOnly ReturnDate,
    decimal? PreviousMonthIbc,
    IReadOnlyList<VacationQuincenaDto> Quincenas,
    VacationPaidInDto? PaidIn,
    IReadOnlyList<ExternalVacationMonthDto> ExternalMonths,
    long Version,
    VacationCanDto Can);

/// <param name="BusinessDays">El calculado por QEP; con <c>External</c> es sólo la referencia junto al tecleado.</param>
/// <param name="Months">Los <c>yyyy-MM</c> que tocan, en orden: los que el diálogo ofrece para los montos de Siigo.</param>
/// <param name="PaidIn">Nulo con <c>External</c>.</param>
public sealed record VacationPreviewDto(
    int BusinessDays,
    DateOnly ReturnDate,
    IReadOnlyList<VacationQuincenaDto> Quincenas,
    IReadOnlyList<string> Months,
    QuincenaRefDto? PaidIn);

/// <param name="Days">Los días de nómina de las vacaciones en esa quincena.</param>
public sealed record PeriodVacationDto(Guid EmployeeId, string FullName, DateOnly StartDate, DateOnly EndDate, int Days);
