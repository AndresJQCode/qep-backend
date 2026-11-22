namespace Modules.Hr.Domain;

/// <summary>
/// Los datos personales y de seguridad social de un empleado. Llega validado: <c>Country</c> ya resuelto (nunca nulo, alfa-2 en
/// mayúsculas) y cruzado con el documento por <c>EmployeeCountry</c> (Application).
/// </summary>
public sealed record EmployeeProfile(
    string FirstName,
    string LastName,
    HrDocumentType DocumentType,
    string DocumentNumber,
    string Country,
    DateOnly? BirthDate,
    string? Phone,
    string? PersonalEmail,
    string? WorkEmail,
    string? Address,
    Guid? CityId,
    Guid? MemberId,
    string? HealthProvider,
    string? PensionFund,
    string? SeveranceFund,
    string? OccupationalRiskProvider,
    ContributorSubtype ContributorSubtype,
    bool ForeignPensionExempt,
    IReadOnlyList<DayOfWeek> RestDays);
