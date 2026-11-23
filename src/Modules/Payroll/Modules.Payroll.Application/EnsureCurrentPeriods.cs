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

    /// <summary>
    /// Lo que abre «Abrir la quincena siguiente»: una quincena más por delante en cada llamada, sin huecos, y nunca más allá de la
    /// segunda quincena del mes siguiente al de hoy (así se pueden dejar listas las dos del mes que viene; una quincena no se borra,
    /// por eso hay tope). Con quincenas, desde la siguiente a la última; sin ninguna, desde la actual si es primera y si no desde la
    /// siguiente (una empresa siempre arranca en una primera quincena). Si la última está detrás de hoy, llena el hueco hasta la
    /// siguiente a la actual.
    /// </summary>
    public static IReadOnlyList<(DateOnly Start, DateOnly End, PayrollHalf Half)> MissingThroughNext(
        IReadOnlyList<PayrollPeriod> existing, DateOnly today)
    {
        var missing = new List<(DateOnly Start, DateOnly End, PayrollHalf Half)>();
        if (OpenNextRange(existing, today) is not var (next, target))
        {
            return missing;
        }

        while (next.Start <= target.Start)
        {
            missing.Add(next);
            next = QuincenaCalendar.After(next.Start);
        }

        return missing;
    }

    /// <summary>El inicio de la quincena que abriría «Abrir la quincena siguiente»; nulo si ya se llegó al tope.</summary>
    public static DateOnly? NextPeriodStartsOn(IReadOnlyList<PayrollPeriod> existing, DateOnly today) =>
        OpenNextRange(existing, today)?.Target.Start;

    private static ((DateOnly Start, DateOnly End, PayrollHalf Half) Next, (DateOnly Start, DateOnly End, PayrollHalf Half) Target)?
        OpenNextRange(IReadOnlyList<PayrollPeriod> existing, DateOnly today)
    {
        var current = QuincenaCalendar.Containing(today);
        var next = existing.Count == 0
            ? (current.Half == PayrollHalf.First ? current : QuincenaCalendar.After(current.Start))
            : QuincenaCalendar.After(existing.Max(period => period.StartDate));
        var target = next.Start > current.Start ? next : QuincenaCalendar.After(current.Start);
        var limit = new DateOnly(today.Year, today.Month, 16).AddMonths(1);
        return target.Start <= limit ? (next, target) : null;
    }

    /// <summary>El período cuyo rango contiene el día del tenant; nulo si no existe (p. ej. sólo hay uno abierto por adelantado).</summary>
    public static Guid? CurrentPeriodId(IReadOnlyList<PayrollPeriod> existing, DateOnly today) =>
        existing.FirstOrDefault(period => period.StartDate <= today && today <= period.EndDate)?.Id.Value;

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
        existing = await PeriodOpener.OpenAsync(missing, existing, command.TenantId, command.CompanyId, calendar.UtcNow, executionContext, periods, unitOfWork, auditPublisher, actors, cancellationToken);

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

        return new PayrollPeriodListDto(PayrollCompanies.ToDto(company), items, PeriodPlan.FirstPeriodStartsOn(periods, today),
            PeriodPlan.NextPeriodStartsOn(periods, today),
            PeriodPlan.CurrentPeriodId(periods, today));
    }
}

/// <summary>Abre las quincenas dadas, con su auditoría, y relee lo que quedó (misma carrera que <c>ensure-current</c>).</summary>
internal static class PeriodOpener
{
    public static async Task<IReadOnlyList<PayrollPeriod>> OpenAsync(
        IReadOnlyList<(DateOnly Start, DateOnly End, PayrollHalf Half)> missing,
        IReadOnlyList<PayrollPeriod> existing,
        Guid tenantId,
        Guid companyId,
        DateTimeOffset utcNow,
        IExecutionContext executionContext,
        IPayrollPeriodRepository periods,
        IPayrollUnitOfWork unitOfWork,
        IPayrollAuditPublisher auditPublisher,
        PeriodActors actors,
        CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return existing;
        }

        var actor = await actors.CurrentAsync(tenantId, cancellationToken);
        foreach (var quincena in missing)
        {
            var period = PayrollPeriod.Open(PayrollPeriodId.New(), tenantId, companyId, quincena, actor, utcNow);
            periods.Add(period);
            PayrollSupport.PublishAudit(
                auditPublisher, executionContext, tenantId, PayrollAuditActions.PeriodCreated,
                PayrollAuditActions.Resources.Period, period.Id.ToString(), ["startDate", "endDate", "half"], utcNow);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ResourceConflictException exception) when (exception.Code == PayrollErrorCodes.PeriodAlreadyExists)
        {
            // Otra solicitud abrió alguna a la vez: lo que quedó es lo que se responde (nada de esto sale por HTTP).
        }

        return await periods.ListAsync(tenantId, companyId, cancellationToken);
    }
}

public sealed record OpenNextPeriodCommand(Guid TenantId, Guid CompanyId) : ICommand<PayrollPeriodListDto>;

/// <summary>
/// Abre una quincena más por delante (y las que falten hasta ella) para liquidar por adelantado, hasta la segunda quincena del mes
/// siguiente. Mismas reglas que <c>ensure-current</c>; en el tope no abre nada y responde la lista.
/// </summary>
public sealed class OpenNextPeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollPeriodRepository periods,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    PeriodActors actors,
    PeriodComposer composer)
    : ICommandHandler<OpenNextPeriodCommand, PayrollPeriodListDto>
{
    public async Task<PayrollPeriodListDto> HandleAsync(OpenNextPeriodCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var company = await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        PayrollCompanies.EnsureActive(company, "An inactive company cannot open payroll periods.");

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var existing = await periods.ListAsync(command.TenantId, command.CompanyId, cancellationToken);
        var missing = PeriodPlan.MissingThroughNext(existing, calendar.Today);
        existing = await PeriodOpener.OpenAsync(
            missing, existing, command.TenantId, command.CompanyId, calendar.UtcNow, executionContext, periods, unitOfWork,
            auditPublisher, actors, cancellationToken);

        return await ListComposer.ComposeAsync(
            composer, actors, command.TenantId, existing, company, calendar.Today, cancellationToken);
    }
}
