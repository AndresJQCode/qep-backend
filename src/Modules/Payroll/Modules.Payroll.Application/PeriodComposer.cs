using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>
/// Arma el encabezado y el detalle de un período. En Borrador, Liquidado y Reabierto los empleados salen de Hr con
/// <see cref="PayrollEligibility"/> (SMMLV a la fecha de inicio) más lo guardado en la última liquidación; en Aprobado y Cerrado
/// sólo se usa lo guardado y los excluidos de la foto: Hr ya no manda (R-16).
/// </summary>
public sealed class PeriodComposer(
    IPayrollHrDirectory hr,
    IPayrollLegalValues legalValues,
    IPayrollResultStore results,
    PayrollCompanies companies,
    ConceptCatalog catalog,
    RecurringConceptComposer recurring,
    PeriodApprovals approvals,
    IVacationRepository vacations,
    VacationComposer vacationComposer)
{
    private sealed record View(
        IReadOnlyList<PayrollEmployeeRowDto> Rows,
        IReadOnlyList<ExcludedEmployeeDto> Excluded,
        IReadOnlyList<BlockedEmployeeDto> Blocked,
        IReadOnlyList<EmployeeResult> Results);

    public async Task<PayrollPeriodDetailDto> ComposeAsync(
        PayrollPeriod period,
        PayrollActor caller,
        IReadOnlySet<string> callerPermissions,
        CancellationToken cancellationToken)
    {
        var view = await BuildViewAsync(period, cancellationToken);
        var header = await HeaderAsync(period, view, caller, callerPermissions, cancellationToken);
        var history = period.Events
            .OrderByDescending(evt => evt.OccurredAt)
            .ThenByDescending(evt => evt.Sequence)
            .Select(ToDto)
            .ToList();

        return new PayrollPeriodDetailDto(header, view.Rows, view.Excluded, view.Blocked, history, await VacationsOfAsync(period, view, cancellationToken));
    }

    /// <summary>
    /// El empleado en la quincena: su fila, lo capturado, sus recurrentes y el resultado guardado. Un excluido responde su fila sin
    /// capturas ni resultado (I-13); sólo quien no tiene contrato de la empresa en la quincena (o, en Aprobado y Cerrado, no está en la
    /// foto) es 404.
    /// </summary>
    public async Task<PayrollEmployeeDetailDto> ComposeEmployeeAsync(
        PayrollPeriod period,
        Guid employeeId,
        PayrollActor caller,
        IReadOnlySet<string> callerPermissions,
        CancellationToken cancellationToken)
    {
        var view = await BuildViewAsync(period, cancellationToken);
        var header = await HeaderAsync(period, view, caller, callerPermissions, cancellationToken);

        var row = view.Rows.FirstOrDefault(candidate => candidate.EmployeeId == employeeId);
        if (row is null)
        {
            var excluded = view.Excluded.FirstOrDefault(candidate => candidate.EmployeeId == employeeId)
                ?? throw PayrollNotFound.EmployeeNotInPeriod(employeeId);
            return new PayrollEmployeeDetailDto(
                header,
                new PayrollEmployeeRowDto(
                    excluded.EmployeeId, excluded.DocumentType, excluded.DocumentNumber, excluded.FullName,
                    null, null, null, false, false, false, null, null),
                EmployeeInputsDto.Empty,
                [],
                null,
                await vacationComposer.TouchingAsync(period, employeeId, cancellationToken));
        }

        var concepts = await catalog.LoadAsync(period.TenantId, period.CompanyId, cancellationToken);
        var mine = period.Inputs.Where(input => input.EmployeeId == employeeId).ToList();

        IReadOnlyList<InputLineDto> Lines(ConceptKind kind) => mine
            .Where(input => input.Kind == kind)
            .OrderBy(input => input.Position)
            .Select(input => new InputLineDto(
                input.ConceptId,
                concepts.Find(input.ConceptId)?.Name ?? string.Empty,
                input.Kind.ToString(),
                input.Hours,
                input.Amount,
                input.SalaryConstitutive))
            .ToList();

        var saved = view.Results.FirstOrDefault(result => result.EmployeeId == employeeId);
        return new PayrollEmployeeDetailDto(
            header,
            row,
            new EmployeeInputsDto(Lines(ConceptKind.Overtime), Lines(ConceptKind.Income), Lines(ConceptKind.Deduction)),
            await recurring.ListAsync(period.TenantId, period.CompanyId, employeeId, cancellationToken),
            saved is null ? null : PayrollResultMapper.ToDto(saved, PayrollResultMapper.ParseLegal(period.LegalValuesJson)),
            await vacationComposer.TouchingAsync(period, employeeId, cancellationToken));
    }

    /// <summary>Sólo el encabezado: los de la lista y el de cada respuesta de capturas.</summary>
    public async Task<PayrollPeriodDto> ComposeHeaderAsync(
        PayrollPeriod period,
        PayrollActor caller,
        IReadOnlySet<string> callerPermissions,
        CancellationToken cancellationToken) =>
        await HeaderAsync(period, await BuildViewAsync(period, cancellationToken), caller, callerPermissions, cancellationToken);

    private async Task<View> BuildViewAsync(PayrollPeriod period, CancellationToken cancellationToken)
    {
        var stored = await results.ListAsync(period.TenantId, period.Id, cancellationToken);
        var withInputs = period.Inputs.Select(input => input.EmployeeId).ToHashSet();

        if (period.Status is PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed)
        {
            return new View(
                SortedRows(stored.Select(result => RowOf(result, withInputs.Contains(result.EmployeeId))).ToList()),
                PeriodExcluded.Parse(period.ExcludedJson)
                    .OrderBy(entry => entry.FullName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(entry => entry.EmployeeId)
                    .ToList(),
                [],
                stored);
        }

        var smmlv = (await PayrollResultMapper.LegalOfAsync(period, legalValues, cancellationToken)).Smmlv.Value;
        var employees = await hr.ListEmployeesAsync(
            period.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken);
        var storedByEmployee = stored.ToDictionary(result => result.EmployeeId);
        var vacationsByEmployee = (await vacations.ListOverlappingAsync(
                period.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 1), period.EndDate, cancellationToken))
            .ToLookup(item => item.EmployeeId);

        var rows = new List<PayrollEmployeeRowDto>(employees.Count);
        var excluded = new List<ExcludedEmployeeDto>();
        var blocked = new List<BlockedEmployeeDto>();
        foreach (var employee in employees)
        {
            var fullName = FullNameOf(employee.FirstName, employee.LastName);
            var eligibility = PayrollEligibility.WithVacations(
                PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, smmlv), vacationsByEmployee[employee.EmployeeId]);
            if (eligibility.Status == EligibilityStatus.Excluded)
            {
                excluded.Add(new ExcludedEmployeeDto(
                    employee.EmployeeId, employee.DocumentType, employee.DocumentNumber, fullName, eligibility.Exclusion!.Value.ToString()));
                continue;
            }

            // Los bloqueados también son una fila (I-9): se les captura desde ahí. `blocked` sólo repite el motivo.
            if (eligibility.Status == EligibilityStatus.Blocked)
            {
                blocked.Add(new BlockedEmployeeDto(employee.EmployeeId, eligibility.Blockers.Select(reason => reason.ToString()).ToList()));
            }

            var saved = storedByEmployee.GetValueOrDefault(employee.EmployeeId);
            var assignment = eligibility.AssignmentAtEnd;
            var pending = eligibility.Contract?.EndedOn is { } ended && ended >= period.StartDate && ended <= period.EndDate;
            rows.Add(new PayrollEmployeeRowDto(
                employee.EmployeeId,
                employee.DocumentType,
                employee.DocumentNumber,
                fullName,
                assignment?.PositionName ?? saved?.Position,
                assignment is null ? saved?.Workplace : WorkplaceLabel(assignment),
                saved?.Liquidation.WorkedDays,
                withInputs.Contains(employee.EmployeeId),
                saved?.Liquidation.FinalSettlementPending ?? pending,
                saved?.Liquidation.NegativeNet ?? false,
                saved is null ? null : PayrollTotalsDto.From(saved.Liquidation.Summary),
                saved?.Liquidation.VacationDays));
        }

        return new View(
            SortedRows(rows),
            excluded.OrderBy(entry => entry.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.EmployeeId).ToList(),
            blocked,
            stored);
    }

    // The vacations that touch the quincena, for the dialog of liquidating: the days are the payroll days in THIS quincena. The names come from
    // the rows (the snapshot in an Approved or Closed period) and the excluded: Hr is not asked again.
    private async Task<IReadOnlyList<PeriodVacationDto>> VacationsOfAsync(PayrollPeriod period, View view, CancellationToken cancellationToken)
    {
        var names = view.Excluded.ToDictionary(entry => entry.EmployeeId, entry => entry.FullName);
        foreach (var row in view.Rows)
        {
            names[row.EmployeeId] = row.FullName;
        }

        return (await vacations.ListOverlappingAsync(period.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken))
            .Where(vacation => names.ContainsKey(vacation.EmployeeId))
            .Select(vacation => new PeriodVacationDto(
                vacation.EmployeeId,
                names[vacation.EmployeeId],
                vacation.StartDate,
                vacation.EndDate,
                VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate).Single(days => days.Start == period.StartDate).Days))
            .OrderBy(item => item.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.StartDate)
            .ThenBy(item => item.EmployeeId)
            .ToList();
    }

    private async Task<PayrollPeriodDto> HeaderAsync(
        PayrollPeriod period,
        View view,
        PayrollActor caller,
        IReadOnlySet<string> permissions,
        CancellationToken cancellationToken)
    {
        var company = await companies.RequireAsync(period.TenantId, period.CompanyId, cancellationToken);
        var state = await approvals.StateAsync(period, withSource: true, cancellationToken);
        var sourceChanged = state.SourceChanged;
        var blockers = period.ApproveBlockers(state.ContextFor(caller, view.Results.Any(result => result.Liquidation.NegativeNet)));

        var manage = permissions.Contains(PayrollPermissions.PeriodManage);
        var editable = period.Status is PayrollPeriodStatus.Draft or PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Reopened;
        var can = new PayrollPeriodCanDto(
            Capture: editable && manage,
            Liquidate: editable && manage && state.FirstHalfLiquidated,
            Approve: blockers.Count == 0 && permissions.Contains(PayrollPermissions.PeriodApprove),
            Close: period.Status == PayrollPeriodStatus.Approved && !period.Outdated && manage,
            Reopen: period.Status is PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed && manage,
            Export: period.Status != PayrollPeriodStatus.Draft && permissions.Contains(PayrollPermissions.PeriodRead));

        return new PayrollPeriodDto(
            period.Id.Value,
            PayrollCompanies.ToDto(company),
            period.StartDate,
            period.EndDate,
            period.Half.ToString(),
            period.Status.ToString(),
            period.Outdated,
            sourceChanged,
            period.Version,
            period.LegalValuesDate,
            period.EngineVersion,
            period.LastLiquidatedAt,
            ActorOf(period.LastLiquidatedBy, period.LastLiquidatedByName),
            period.ApprovedAt,
            ActorOf(period.ApprovedBy, period.ApprovedByName),
            period.ClosedAt,
            ActorOf(period.ClosedBy, period.ClosedByName),
            view.Rows.Count,
            view.Results.Count == 0
                ? null
                : PayrollTotalsDto.From(PayrollSummary.Sum(view.Results.Select(result => result.Liquidation.Summary))),
            can,
            blockers);
    }

    private static PayrollEmployeeRowDto RowOf(EmployeeResult result, bool hasInputs) => new(
        result.EmployeeId,
        result.DocumentType,
        result.DocumentNumber,
        result.FullName,
        result.Position,
        result.Workplace,
        result.Liquidation.WorkedDays,
        hasInputs,
        result.Liquidation.FinalSettlementPending,
        result.Liquidation.NegativeNet,
        PayrollTotalsDto.From(result.Liquidation.Summary),
        result.Liquidation.VacationDays);

    private static List<PayrollEmployeeRowDto> SortedRows(List<PayrollEmployeeRowDto> rows) => rows
        .OrderBy(row => row.FullName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(row => row.EmployeeId)
        .ToList();

    private static string FullNameOf(string firstName, string lastName) => $"{firstName} {lastName}".Trim();

    private static string WorkplaceLabel(HrAssignmentSource assignment) => $"{assignment.WorkplaceName} - {assignment.SiteName}";

    private static ActorDto? ActorOf(Guid? subjectId, string? name) =>
        subjectId is { } id ? new ActorDto(id, name ?? PeriodActors.NoName) : null;

    private static PeriodEventDto ToDto(PeriodEvent evt) => new(
        evt.Id.Value,
        evt.OccurredAt,
        new ActorDto(evt.ActorSubjectId, evt.ActorName),
        evt.Action.ToString(),
        evt.FromStatus?.ToString(),
        evt.ToStatus?.ToString(),
        evt.Reason,
        evt.AbsencesConfirmed);
}
