using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>Lo que el alta y la edición del empleado comprueban contra lo cargado, y cómo se arma el perfil del dominio.</summary>
internal static class EmployeeRules
{
    public static Task<Guid?> EnsureCityAsync(IHrGeographyLookup geography, Guid? cityId, CancellationToken cancellationToken) =>
        HrSettingsSupport.EnsureCityAsync(geography, cityId, cancellationToken);

    /// <summary>
    /// La membresía tiene que ser activa y del tenant: el directorio sólo devuelve las del tenant, así que una de otro tenant es
    /// «no existe». Que otro empleado ya la tenga es un 409 que sale del índice único al guardar.
    /// </summary>
    public static async Task<Guid?> EnsureMemberAsync(
        IHrMemberDirectory members, Guid tenantId, Guid? memberId, CancellationToken cancellationToken)
    {
        if (memberId is not { } id)
        {
            return null;
        }

        if (await members.FindAsync(tenantId, [id], cancellationToken) is not [{ IsActive: true }, ..])
        {
            throw new HrDomainException(HrErrorCodes.MemberInvalid, "The member does not exist in this tenant or is not active.");
        }

        return id;
    }

    public static void EnsureBirthDate(DateOnly? birthDate, DateOnly today)
    {
        if (birthDate is { } date && date >= today)
        {
            HrFieldErrors.Throw("birthDate", "La fecha de nacimiento tiene que ser anterior a hoy.");
        }
    }

    /// <summary>El perfil del dominio desde el cuerpo ya validado. <paramref name="country"/> es el efectivo y ya está cruzado.</summary>
    public static EmployeeProfile ToProfile(EmployeeRequest request, string country) => new(
        request.FirstName!.Trim(),
        request.LastName!.Trim(),
        Enum.Parse<HrDocumentType>(request.DocumentType!),
        request.DocumentNumber!.Trim(),
        country,
        request.BirthDate,
        HrText.Clean(request.Phone),
        HrText.Clean(request.PersonalEmail),
        HrText.Clean(request.WorkEmail),
        HrText.Clean(request.Address),
        request.CityId,
        request.MemberId,
        HrText.Clean(request.HealthProvider),
        HrText.Clean(request.PensionFund),
        HrText.Clean(request.SeveranceFund),
        HrText.Clean(request.OccupationalRiskProvider),
        request.ContributorSubtype is null ? ContributorSubtype.Standard : Enum.Parse<ContributorSubtype>(request.ContributorSubtype),
        request.ForeignPensionExempt ?? false,
        request.RestDays is null
            ? [DayOfWeek.Sunday]
            : request.RestDays.Select(Enum.Parse<DayOfWeek>).OrderBy(day => ((int)day + 6) % 7).ToList());

    public static EmployeeProfile ProfileOf(Employee employee) => new(
        employee.FirstName,
        employee.LastName,
        employee.DocumentType,
        employee.DocumentNumber,
        employee.Country,
        employee.BirthDate,
        employee.Phone,
        employee.PersonalEmail,
        employee.WorkEmail,
        employee.Address,
        employee.CityId,
        employee.MemberId,
        employee.HealthProvider,
        employee.PensionFund,
        employee.SeveranceFund,
        employee.OccupationalRiskProvider,
        employee.ContributorSubtype,
        employee.ForeignPensionExempt,
        employee.RestDays);

    public static PaymentDetails? ToPayment(PaymentRequest? request) => request is { PaymentMethod: { } method }
        ? new PaymentDetails(
            Enum.Parse<PaymentMethod>(method),
            HrText.Clean(request.BankName),
            request.BankAccountType is null ? null : Enum.Parse<BankAccountType>(request.BankAccountType),
            HrText.Clean(request.BankAccountNumber))
        : null;
}
