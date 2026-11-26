using BuildingBlocks.Application;
using FluentValidation;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record PreviewVacationQuery(Guid TenantId, Guid CompanyId, Guid EmployeeId, VacationPreviewRequest Request)
    : IQuery<VacationPreviewDto>;

/// <summary>
/// Cuenta los días hábiles y las quincenas antes de guardar, para que el diálogo diga «15 días hábiles, regresa el 6 de enero» sin
/// contar en el navegador. No escribe. Con <c>Qep</c> responde los 422 de fechas; nunca <c>overlap</c> ni bloqueos, porque no sabe
/// qué vacaciones se están editando.
/// </summary>
public sealed class PreviewVacationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    VacationRules rules,
    IValidator<VacationPreviewRequest> validator)
    : IQueryHandler<PreviewVacationQuery, VacationPreviewDto>
{
    public async Task<VacationPreviewDto> HandleAsync(PreviewVacationQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        await validator.ValidateAndThrowAsync(query.Request, cancellationToken);

        var request = query.Request;
        var origin = Enum.Parse<VacationOrigin>(request.Origin!);
        var start = request.StartDate!.Value;
        var end = request.EndDate!.Value;
        var facts = await rules.CheckAsync(
            query.TenantId, query.CompanyId, query.EmployeeId, origin, start, end, cancellationToken);

        QuincenaRefDto? paidIn = null;
        if (origin == VacationOrigin.Qep)
        {
            var paying = QuincenaCalendar.Containing(start);
            paidIn = new QuincenaRefDto(paying.Start, paying.End);
        }

        return new VacationPreviewDto(
            facts.ComputedBusinessDays,
            facts.ReturnDate,
            facts.Quincenas.Select(days => new VacationQuincenaDto(days.Start, days.End, days.Days)).ToList(),
            facts.Months,
            paidIn);
    }
}
