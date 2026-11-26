using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class EmployeeActivityLog(HrDbContext dbContext) : IEmployeeActivityLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Append(EmployeeActivityEntry entry) =>
        dbContext.Activity.Add(new EmployeeActivityRow
        {
            Id = entry.Id,
            TenantId = entry.TenantId,
            EmployeeId = entry.EmployeeId,
            OccurredAt = entry.OccurredAt,
            ActorSubjectId = entry.ActorSubjectId,
            ActorName = entry.ActorName,
            Action = entry.Action,
            Category = entry.Category,
            ContractId = entry.ContractId,
            Changes = JsonSerializer.Serialize(entry.Changes, Json),
        });

    public async Task<(IReadOnlyList<EmployeeActivityEntry> Items, int Total)> ListAsync(
        Guid tenantId,
        EmployeeId id,
        IReadOnlySet<HrActivityCategory> categories,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var allowed = categories.ToArray();
        var rows = dbContext.Activity
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.EmployeeId == id && allowed.Contains(row.Category));

        var total = await rows.CountAsync(cancellationToken);
        var pageRows = await rows
            .OrderByDescending(row => row.OccurredAt)
            .ThenByDescending(row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = pageRows
            .Select(row => new EmployeeActivityEntry(
                row.Id,
                row.TenantId,
                row.EmployeeId,
                row.OccurredAt,
                row.ActorSubjectId,
                row.ActorName,
                row.Action,
                row.Category,
                row.ContractId,
                JsonSerializer.Deserialize<List<ActivityChange>>(row.Changes, Json) ?? []))
            .ToList();
        return (items, total);
    }
}
