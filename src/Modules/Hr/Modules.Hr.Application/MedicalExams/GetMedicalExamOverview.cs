using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <param name="Status">Null, or one or more <see cref="MedicalExamStatus"/> names separated by commas.</param>
public sealed record GetMedicalExamOverviewQuery(Guid TenantId, string? Status, string? Search, int Page, int PageSize)
    : IQuery<MedicalExamOverviewDto>
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed record MedicalExamRefDto(string Type, DateOnly ExamDate);

/// <param name="NextDue">The deadline of the pending exit exam, or else the next periodic exam; null when there is none.</param>
/// <param name="BusinessDaysLeft">Only for <c>ExitPending</c>, counted by the backend (0 once due or overdue).</param>
public sealed record MedicalExamOverviewItemDto(
    Guid EmployeeId,
    string FullName,
    string DocumentType,
    string DocumentNumber,
    string? PositionName,
    MedicalExamRefDto? LastExam,
    DateOnly? NextDue,
    string Status,
    int? BusinessDaysLeft);

/// <summary>
/// The control screen (spec 2026-11-23 §7.2). <c>Counts</c> always carries the six statuses, in zero too, and ignores the
/// <c>status</c> and <c>search</c> filters: they are the totals of the tenant, so the tabs do not move when filtering.
/// </summary>
public sealed record MedicalExamOverviewDto(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<MedicalExamOverviewItemDto> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>One employee of the universe, with the status already resolved by <see cref="MedicalExamRules.StatusOf"/>.</summary>
public sealed record MedicalExamOverviewRow(
    EmployeeId EmployeeId,
    string FirstName,
    string LastName,
    HrDocumentType DocumentType,
    string DocumentNumber,
    string? PositionName,
    MedicalExamType? LastExamType,
    DateOnly? LastExamDate,
    DateOnly? NextDue,
    MedicalExamStatus Status);

public sealed record MedicalExamOverviewPage(
    IReadOnlyDictionary<MedicalExamStatus, int> Counts, int Total, IReadOnlyList<MedicalExamOverviewRow> Rows);

/// <summary>
/// Reads the control universe (spec 2026-11-23 §5.4): the active employees plus the retired ones with a pending exit exam. The
/// implementation resolves the status, filters, orders and pages <b>in the database</b>: a tenant with thousands of employees never
/// loads them all into memory.
/// </summary>
public interface IMedicalExamOverviewReader
{
    Task<MedicalExamOverviewPage> ReadAsync(
        Guid tenantId,
        IReadOnlyCollection<MedicalExamStatus> statuses,
        string? search,
        DateOnly today,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed class GetMedicalExamOverviewQueryValidator : HrValidator<GetMedicalExamOverviewQuery>
{
    public GetMedicalExamOverviewQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).WithMessage("La página empieza en 1.");
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, GetMedicalExamOverviewQuery.MaxPageSize)
            .WithMessage($"El tamaño de página va de 1 a {GetMedicalExamOverviewQuery.MaxPageSize}.");
    }
}

public sealed class GetMedicalExamOverviewHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMedicalExamOverviewReader reader,
    IHrBusinessCalendar businessCalendar,
    IValidator<GetMedicalExamOverviewQuery> validator)
    : IQueryHandler<GetMedicalExamOverviewQuery, MedicalExamOverviewDto>
{
    public async Task<MedicalExamOverviewDto> HandleAsync(GetMedicalExamOverviewQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.MedicalRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var statuses = ParseStatuses(query.Status);

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var page = await reader.ReadAsync(query.TenantId, statuses, query.Search, today, query.Page, query.PageSize, cancellationToken);

        // The calendar is resolved once per request, and only if some row needs it.
        IHrBusinessDayCounter? counter = page.Rows.Any(row => row.Status == MedicalExamStatus.ExitPending)
            ? await businessCalendar.CounterAsync(query.TenantId, cancellationToken)
            : null;
        var items = new List<MedicalExamOverviewItemDto>(page.Rows.Count);
        foreach (var row in page.Rows)
        {
            int? left = row.Status == MedicalExamStatus.ExitPending && row.NextDue is { } due
                ? due < today ? 0 : counter!.Count(today, due)
                : null;
            items.Add(new MedicalExamOverviewItemDto(
                row.EmployeeId.Value,
                $"{row.FirstName} {row.LastName}",
                row.DocumentType.ToString(),
                row.DocumentNumber,
                row.PositionName,
                row.LastExamType is { } type && row.LastExamDate is { } date ? new MedicalExamRefDto(type.ToString(), date) : null,
                row.NextDue,
                row.Status.ToString(),
                left));
        }

        // The six statuses always travel, in zero too: a missing one would make the screen know the enum to draw it.
        var counts = Enum.GetValues<MedicalExamStatus>().ToDictionary(status => status.ToString(), status => page.Counts.GetValueOrDefault(status));

        return new MedicalExamOverviewDto(counts, items, query.Page, query.PageSize, page.Total);
    }

    private static List<MedicalExamStatus> ParseStatuses(string? text)
    {
        var statuses = new List<MedicalExamStatus>();
        foreach (var name in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!HrEnums.TryParse<MedicalExamStatus>(name, out var status))
            {
                throw new HrDomainException(
                    HrErrorCodes.MedicalExamStatusInvalid,
                    $"'{name}' is not a medical exam status.",
                    new Dictionary<string, string[]>
                    {
                        ["status"] = ["El estado no es válido: usa ExitPending, NoExams, Overdue, DueSoon, UpToDate o NotRequired."],
                    });
            }

            statuses.Add(status);
        }

        return statuses;
    }
}
