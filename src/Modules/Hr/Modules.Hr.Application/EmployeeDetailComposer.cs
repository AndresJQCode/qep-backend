using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Arma las respuestas de empleados (calco de <c>LoanDtoComposer</c>): el <see cref="EmployeeDetailDto"/> de leer y de toda escritura,
/// y la página de la lista. Cada respuesta pide configuración, ciudades, miembros y empresas <b>una vez</b>, sin importar cuántos
/// registros traiga. El enmascarado vive acá y en ningún otro lugar: quien llama decide <c>canReadContract</c> con su propio permiso,
/// también en toda escritura.
/// </summary>
public sealed class EmployeeDetailComposer(
    IHrSettingsRepository settings,
    HrCompanyRefs companies,
    IHrGeographyLookup geography,
    IHrMemberDirectory members)
{
    /// <summary>Lo que el directorio no devuelve (la membresía se quitó del todo) se pinta así, nunca se omite.</summary>
    internal const string UnknownMemberName = "Sin nombre";

    private sealed record Lookups(
        IReadOnlyDictionary<Guid, SettingRefDto> Settings, IReadOnlyDictionary<Guid, CompanyRefDto> Companies);

    public async Task<EmployeeDetailDto> ComposeAsync(
        Employee employee, bool canReadContract, DateOnly today, CancellationToken cancellationToken)
    {
        var tenantId = employee.TenantId;
        MunicipalityRefDto? city = null;
        if (employee.CityId is { } cityId
            && (await geography.FindCitiesAsync([cityId], cancellationToken)).GetValueOrDefault(cityId) is { } found)
        {
            city = new MunicipalityRefDto(found.CityId, found.Name, found.DepartmentId, found.DepartmentName);
        }

        MemberRefDto? member = null;
        if (employee.MemberId is { } memberId)
        {
            member = await members.FindAsync(tenantId, [memberId], cancellationToken) is [var resolved, ..]
                ? new MemberRefDto(resolved.MemberId, resolved.DisplayName, resolved.IsActive)
                : new MemberRefDto(memberId, UnknownMemberName, false);
        }

        ContractDto? current = null;
        var previous = new List<ContractDto>();
        if (canReadContract && employee.Contracts.Count > 0)
        {
            var lookups = await LoadLookupsAsync(tenantId, employee.Contracts.Select(contract => contract.CompanyId), cancellationToken);
            current = employee.ActiveContract is { } active ? ToContractDto(active, lookups, today) : null;
            previous = employee.Contracts
                .Where(contract => contract.Status != ContractStatus.Active)
                .OrderByDescending(contract => contract.StartDate)
                .Select(contract => ToContractDto(contract, lookups, today))
                .ToList();
        }

        return new EmployeeDetailDto(
            ToEmployeeDto(employee, city, member, canReadContract),
            canReadContract,
            employee.CanDelete,
            current,
            previous);
    }

    public async Task<EmployeePageDto> ComposePageAsync(
        EmployeeListPage page, int pageNumber, int pageSize, bool canReadContract, DateOnly today, CancellationToken cancellationToken)
    {
        Lookups? lookups = null;
        var active = page.Items.Select(employee => employee.ActiveContract).OfType<EmploymentContract>().ToList();
        if (canReadContract && active.Count > 0)
        {
            lookups = await LoadLookupsAsync(page.Items[0].TenantId, active.Select(contract => contract.CompanyId), cancellationToken);
        }

        var items = page.Items.Select(employee =>
        {
            var contract = canReadContract ? employee.ActiveContract : null;
            if (contract is null || lookups is null)
            {
                return new EmployeeListItemDto(
                    employee.Id.Value, employee.FirstName, employee.LastName, employee.DocumentType.ToString(),
                    employee.DocumentNumber, employee.Status.ToString(), null, null, null, null, null, null, null);
            }

            var assignment = EffectiveDated.CurrentOf(contract.Assignments, today);
            return new EmployeeListItemDto(
                employee.Id.Value,
                employee.FirstName,
                employee.LastName,
                employee.DocumentType.ToString(),
                employee.DocumentNumber,
                employee.Status.ToString(),
                lookups.Companies[contract.CompanyId].Name,
                lookups.Settings[assignment.PositionId.Value].Name,
                lookups.Settings[assignment.SiteId.Value].Name,
                contract.ContractType.ToString(),
                contract.Duration.ToString(),
                contract.EndDate,
                FixedTermLimits.NoticeDue(contract, today));
        }).ToList();

        return new EmployeePageDto(
            items,
            page.Total,
            pageNumber,
            pageSize,
            new EmployeeListSummaryDto(page.Active, page.Retired, page.Deactivated, canReadContract ? page.ExpiringSoon : null));
    }

    private async Task<Lookups> LoadLookupsAsync(
        Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken cancellationToken)
    {
        var snapshot = await settings.ListAsync(tenantId, cancellationToken);
        var refs = new Dictionary<Guid, SettingRefDto>();
        foreach (var site in snapshot.Sites)
        {
            refs[site.Id.Value] = new SettingRefDto(site.Id.Value, site.Name, site.Archived);
        }

        foreach (var workplace in snapshot.Workplaces)
        {
            refs[workplace.Id.Value] = new SettingRefDto(workplace.Id.Value, workplace.Name, workplace.Archived);
        }

        foreach (var area in snapshot.Areas)
        {
            refs[area.Id.Value] = new SettingRefDto(area.Id.Value, area.Name, area.Archived);
        }

        foreach (var position in snapshot.Positions)
        {
            refs[position.Id.Value] = new SettingRefDto(position.Id.Value, position.Name, position.Archived);
        }

        return new Lookups(refs, await companies.ResolveAsync(tenantId, companyIds, cancellationToken));
    }

    private static EmployeeDto ToEmployeeDto(Employee employee, MunicipalityRefDto? city, MemberRefDto? member, bool canReadContract) => new(
        employee.Id.Value,
        employee.FirstName,
        employee.LastName,
        employee.DocumentType.ToString(),
        employee.DocumentNumber,
        employee.Country,
        employee.BirthDate,
        employee.Phone,
        employee.PersonalEmail,
        employee.WorkEmail,
        employee.Address,
        city,
        member,
        employee.HealthProvider,
        employee.PensionFund,
        employee.SeveranceFund,
        employee.OccupationalRiskProvider,
        employee.ContributorSubtype.ToString(),
        employee.ForeignPensionExempt,
        employee.RestDays.OrderBy(day => ((int)day + 6) % 7).Select(day => day.ToString()).ToList(),
        canReadContract && employee.Payment is { } payment
            ? new PaymentDto(payment.Method.ToString(), payment.BankName, payment.BankAccountType?.ToString(), payment.BankAccountNumber)
            : null,
        employee.Status.ToString(),
        employee.Version,
        employee.CreatedAt,
        employee.UpdatedAt);

    private static ContractDto ToContractDto(EmploymentContract contract, Lookups lookups, DateOnly today)
    {
        var salary = EffectiveDated.CurrentOf(contract.Salaries, today);
        var assignment = EffectiveDated.CurrentOf(contract.Assignments, today);
        SettingRefDto Ref(Guid id) => lookups.Settings[id];

        return new ContractDto(
            contract.Id.Value,
            lookups.Companies[contract.CompanyId],
            contract.ContractType.ToString(),
            contract.Duration.ToString(),
            contract.SalaryType.ToString(),
            contract.StartDate,
            contract.EndDate,
            contract.Status.ToString(),
            contract.EndedOn,
            contract.ConvertedToIndefiniteOn,
            contract.PreviousContractId?.Value,
            FileOf(contract.DocumentFileId, contract.DocumentFileName),
            contract.TerminationReason is { } reason
                ? new TerminationDto(reason.ToString(), JustCauseOf(reason), contract.EndedOn ?? contract.StartDate, contract.TerminationNotes)
                : null,
            contract.StartDate <= today,
            new ContractCurrentDto(
                salary.Amount,
                salary.Teleworker,
                contract.SalaryType.ToString(),
                Ref(assignment.PositionId.Value),
                assignment.AreaId is { } areaId ? Ref(areaId.Value) : null,
                Ref(assignment.SiteId.Value),
                Ref(assignment.WorkplaceId.Value)),
            FixedTermLimits.NoticeDue(contract, today),
            FixedTermLimits.DaysUntilEnd(contract, today),
            contract.Salaries
                .OrderByDescending(record => record.EffectiveDate)
                .Select(record => new SalaryRecordDto(
                    record.Id.Value,
                    record.EffectiveDate,
                    record.Amount,
                    record.Teleworker,
                    record.BelowMinimumAcknowledged,
                    FileOf(record.DocumentFileId, record.DocumentFileName),
                    EffectiveDated.IsScheduled(record, today),
                    record == salary,
                    record.CreatedAt,
                    record.UpdatedAt))
                .ToList(),
            contract.Assignments
                .OrderByDescending(record => record.EffectiveDate)
                .Select(record => new AssignmentRecordDto(
                    record.Id.Value,
                    record.EffectiveDate,
                    Ref(record.PositionId.Value),
                    record.AreaId is { } area ? Ref(area.Value) : null,
                    Ref(record.SiteId.Value),
                    Ref(record.WorkplaceId.Value),
                    FileOf(record.DocumentFileId, record.DocumentFileName),
                    EffectiveDated.IsScheduled(record, today),
                    record == assignment,
                    record.CreatedAt))
                .ToList(),
            contract.Renewals
                .OrderByDescending(renewal => renewal.CreatedAt)
                .ThenByDescending(renewal => renewal.NewEndDate)
                .Select(renewal => new RenewalDto(
                    renewal.Id.Value,
                    renewal.PreviousEndDate,
                    renewal.NewEndDate,
                    renewal.IsShort,
                    FileOf(renewal.DocumentFileId, renewal.DocumentFileName),
                    renewal.CreatedAt))
                .ToList());
    }

    private static FileRefDto? FileOf(Guid? fileId, string? fileName) =>
        fileId is { } id ? new FileRefDto(id, fileName ?? string.Empty) : null;

    private static bool? JustCauseOf(TerminationReason reason) => reason switch
    {
        TerminationReason.DismissalWithJustCause => true,
        TerminationReason.DismissalWithoutJustCause => false,
        _ => null,
    };
}
