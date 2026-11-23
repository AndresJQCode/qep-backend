using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Qué quincenas faltan (R-13), con el calendario puro: ni Hr ni la base.</summary>
internal static class PeriodPlan
{
    /// <summary>
    /// Todas las que faltan entre la última y la actual del día del tenant, sin huecos. Sin ninguna, la primera es la actual
    /// <b>sólo si es una primera quincena</b>: en una segunda no se crea nada (la empresa empieza el mes que viene).
    /// </summary>
    public static IReadOnlyList<(DateOnly Start, DateOnly End, PayrollHalf Half)> Missing(
        IReadOnlyList<PayrollPeriod> existing, DateOnly today)
    {
        var current = QuincenaCalendar.Containing(today);
        if (existing.Count == 0)
        {
            return current.Half == PayrollHalf.First ? [current] : [];
        }

        var missing = new List<(DateOnly Start, DateOnly End, PayrollHalf Half)>();
        var next = QuincenaCalendar.After(existing.Max(period => period.StartDate));
        while (next.Start <= current.Start)
        {
            missing.Add(next);
            next = QuincenaCalendar.After(next.Start);
        }

        return missing;
    }

    /// <summary>Sin períodos y con hoy en una segunda quincena: el día 1 del mes siguiente (R-13). Si no, nulo.</summary>
    public static DateOnly? FirstPeriodStartsOn(IReadOnlyList<PayrollPeriod> existing, DateOnly today) =>
        existing.Count == 0 && QuincenaCalendar.Containing(today).Half == PayrollHalf.Second
            ? new DateOnly(today.Year, today.Month, 1).AddMonths(1)
            : null;
}

public sealed record EnsureCurrentPeriodsCommand(Guid TenantId, Guid CompanyId) : ICommand<PayrollPeriodListDto>;

/// <summary>
/// Abre las quincenas que faltan de una empresa y responde la lista. Idempotente: dos solicitudes a la vez abren lo mismo, y la que
/// pierde la carrera (índice único <c>IX_periods_tenant_company_start</c>) relee y responde lo que quedó. Exige <c>PeriodRead</c>
/// <b>y</b> <c>PeriodManage</c>: abrir es de quien administra (I-6); quien sólo lee usa <c>GET periods</c>, que no crea nada.
/// </summary>
public sealed class EnsureCurrentPeriodsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollPeriodRepository periods,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    PeriodActors actors,
    PeriodComposer composer)
    : ICommandHandler<EnsureCurrentPeriodsCommand, PayrollPeriodListDto>
{
    public async Task<PayrollPeriodListDto> HandleAsync(EnsureCurrentPeriodsCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var company = await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        PayrollCompanies.EnsureActive(company, "An inactive company cannot open payroll periods.");

        // «Hoy» es el día del tenant, no el UTC: a las 23:30 de Bogotá del 15 todavía es la primera quincena (Review Focus 1).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var existing = await periods.ListAsync(command.TenantId, command.CompanyId, cancellationToken);
        var missing = PeriodPlan.Missing(existing, calendar.Today);

        if (missing.Count > 0)
        {
            var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
            foreach (var quincena in missing)
            {
                var period = PayrollPeriod.Open(PayrollPeriodId.New(), command.TenantId, command.CompanyId, quincena, actor, calendar.UtcNow);
                periods.Add(period);
                PayrollSupport.PublishAudit(
                    auditPublisher, executionContext, command.TenantId, PayrollAuditActions.PeriodCreated,
                    PayrollAuditActions.Resources.Period, period.Id.ToString(), ["startDate", "endDate", "half"], calendar.UtcNow);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ResourceConflictException exception) when (exception.Code == PayrollErrorCodes.PeriodAlreadyExists)
            {
                // Otra solicitud abrió alguna a la vez: lo que quedó es lo que se responde (nada de esto sale por HTTP).
            }

            existing = await periods.ListAsync(command.TenantId, command.CompanyId, cancellationToken);
        }

        return await ListComposer.ComposeAsync(
            composer, actors, command.TenantId, existing, company, calendar.Today, cancellationToken);
    }
}

/// <summary>Arma el <see cref="PayrollPeriodListDto"/> de las dos rutas que lo responden.</summary>
internal static class ListComposer
{
    public static async Task<PayrollPeriodListDto> ComposeAsync(
        PeriodComposer composer,
        PeriodActors actors,
        Guid tenantId,
        IReadOnlyList<PayrollPeriod> periods,
        PayrollCompanyRef company,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var caller = await actors.CurrentAsync(tenantId, cancellationToken);
        var permissions = actors.CallerPermissions();
        var items = new List<PayrollPeriodDto>(periods.Count);
        foreach (var period in periods.OrderByDescending(period => period.StartDate))
        {
            items.Add(await composer.ComposeHeaderAsync(period, caller, permissions, cancellationToken));
        }

        return new PayrollPeriodListDto(PayrollCompanies.ToDto(company), items, PeriodPlan.FirstPeriodStartsOn(periods, today));
    }
}
