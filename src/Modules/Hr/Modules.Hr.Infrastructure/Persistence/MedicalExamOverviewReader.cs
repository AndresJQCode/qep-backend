using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>
/// The control overview (spec 2026-11-23 §5.4 and §7.2) resolved in the database: one query builds the status rank of every employee
/// of the universe, and the counts, the filter, the order and the page all run over it. Only the page comes back to memory.
/// </summary>
/// <remarks>
/// The rank in SQL mirrors <see cref="MedicalExamRules.StatusOf"/> (same order as <see cref="MedicalExamStatus"/>); the status that
/// travels in each row is still the one of that method, applied to the page. A test lines both up. The frequency is that of the
/// position of the assignment in force on the active contract (or the latest one, for a retired employee); its position name too.
/// </remarks>
internal sealed class MedicalExamOverviewReader(HrDbContext dbContext) : IMedicalExamOverviewReader
{
    public async Task<MedicalExamOverviewPage> ReadAsync(
        Guid tenantId,
        IReadOnlyCollection<MedicalExamStatus> statuses,
        string? search,
        DateOnly today,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var soonLimit = today.AddDays(MedicalExamRules.DueSoonDays);
        const int exitPending = (int)MedicalExamStatus.ExitPending;
        const int noExams = (int)MedicalExamStatus.NoExams;
        const int overdue = (int)MedicalExamStatus.Overdue;
        const int dueSoon = (int)MedicalExamStatus.DueSoon;
        const int upToDate = (int)MedicalExamStatus.UpToDate;
        const int notRequired = (int)MedicalExamStatus.NotRequired;

        var universe =
            from employee in dbContext.Employees.AsNoTracking()
            where employee.TenantId == tenantId
            let pendingDue = dbContext.ExitExamRequirements
                .Where(requirement => requirement.EmployeeId == employee.Id && requirement.Status == ExitExamStatus.Pending)
                .Min(requirement => (DateOnly?)requirement.DueDate)
            where employee.Status == EmployeeStatus.Active || (employee.Status == EmployeeStatus.Retired && pendingDue != null)
            let lastDate = dbContext.MedicalExams
                .Where(exam => exam.EmployeeId == employee.Id)
                .Max(exam => (DateOnly?)exam.ExamDate)
            let contractId = dbContext.Contracts
                .Where(contract => contract.EmployeeId == employee.Id)
                .OrderByDescending(contract => contract.Status == ContractStatus.Active)
                .ThenByDescending(contract => contract.StartDate)
                .Select(contract => contract.Id)
                .FirstOrDefault()
            // The assignment in force today; a contract that has not started yet shows its first one (EffectiveDated.CurrentOf).
            let positionId = dbContext.AssignmentRecords
                .Where(assignment => assignment.ContractId == contractId)
                .OrderByDescending(assignment => assignment.EffectiveDate <= today)
                .ThenByDescending(assignment => assignment.EffectiveDate <= today ? assignment.EffectiveDate : DateOnly.MinValue)
                .ThenBy(assignment => assignment.EffectiveDate)
                .Select(assignment => assignment.PositionId)
                .FirstOrDefault()
            let frequency = dbContext.Positions
                .Where(position => position.Id == positionId)
                .Select(position => position.ExamFrequencyMonths)
                .FirstOrDefault()
            let nextPeriodic = lastDate != null && frequency != null ? lastDate.Value.AddMonths(frequency.Value) : (DateOnly?)null
            select new
            {
                employee.Id,
                employee.FirstName,
                employee.LastName,
                employee.DocumentType,
                employee.DocumentNumber,
                EmployeeStatus = employee.Status,
                PendingDue = pendingDue,
                LastDate = lastDate,
                NextPeriodic = nextPeriodic,
                NextDue = pendingDue ?? nextPeriodic,
                PositionName = dbContext.Positions
                    .Where(position => position.Id == positionId)
                    .Select(position => position.Name)
                    .FirstOrDefault(),
                LastType = dbContext.MedicalExams
                    .Where(exam => exam.EmployeeId == employee.Id)
                    .OrderByDescending(exam => exam.ExamDate)
                    .ThenByDescending(exam => exam.CreatedAt)
                    .ThenByDescending(exam => exam.Id)
                    .Select(exam => (MedicalExamType?)exam.Type)
                    .FirstOrDefault(),
                Rank = pendingDue != null ? exitPending
                    : lastDate == null ? noExams
                    : nextPeriodic == null ? notRequired
                    : nextPeriodic < today ? overdue
                    : nextPeriodic <= soonLimit ? dueSoon
                    : upToDate,
            };

        // The counts ignore the status and the search: they are the totals of the tenant.
        var counted = await universe
            .GroupBy(row => row.Rank)
            .Select(group => new { Rank = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var counts = counted.ToDictionary(row => (MedicalExamStatus)row.Rank, row => row.Count);

        var filtered = universe;
        if (statuses.Count > 0)
        {
            var ranks = statuses.Select(status => (int)status).ToArray();
            filtered = filtered.Where(row => ranks.Contains(row.Rank));
        }

        var pattern = LikeSearch.ContainsPattern(search);
        if (pattern is not null)
        {
            filtered = filtered.Where(row =>
                EF.Functions.ILike(row.FirstName + " " + row.LastName, pattern, LikeSearch.EscapeCharacter)
                || EF.Functions.ILike(row.DocumentNumber, pattern, LikeSearch.EscapeCharacter));
        }

        var total = await filtered.CountAsync(cancellationToken);
        var rows = await filtered
            .OrderBy(row => row.Rank)
            .ThenBy(row => row.NextDue)
            .ThenBy(row => row.LastName)
            .ThenBy(row => row.FirstName)
            .ThenBy(row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new MedicalExamOverviewPage(
            counts,
            total,
            rows.Select(row => new MedicalExamOverviewRow(
                row.Id,
                row.FirstName,
                row.LastName,
                row.DocumentType,
                row.DocumentNumber,
                row.PositionName,
                row.LastType,
                row.LastDate,
                row.NextDue,
                MedicalExamRules.StatusOf(row.EmployeeStatus, row.PendingDue is not null, row.LastDate is not null, row.NextPeriodic, today)
                    ?? throw new InvalidOperationException("The universe query returned an employee outside the control universe."))).ToList());
    }
}
