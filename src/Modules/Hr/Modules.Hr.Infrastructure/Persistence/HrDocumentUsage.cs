using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;

namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-26 («Referencias»): las cinco tablas de Hr con <c>document_file_id</c>. LINQ sobre el mapeo, no SQL crudo: renombrar una
/// columna rompe el mapeo, no la pregunta en silencio. Contratos, salarios, cargo y lugar y prórrogas no llevan tenant: se acotan por
/// <c>employees.tenant_id</c> (contrato → empleado; registro → contrato → empleado). Los exámenes médicos, por su propio <c>tenant_id</c>.
/// Los índices parciales <c>IX_&lt;tabla&gt;_document_file_id</c> hacen que cada pregunta no recorra la tabla entera.
/// </summary>
internal sealed class HrDocumentUsage(HrDbContext dbContext) : IHrDocumentUsage
{
    public async Task<IReadOnlySet<Guid>> FindUsedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = fileIds.Distinct().ToList();
        var employees = dbContext.Employees.AsNoTracking().Where(employee => employee.TenantId == tenantId);
        var contracts = dbContext.Contracts.AsNoTracking()
            .Join(employees, contract => contract.EmployeeId, employee => employee.Id, (contract, _) => contract);

        var fromContracts = contracts
            .Where(contract => contract.DocumentFileId != null && ids.Contains(contract.DocumentFileId.Value))
            .Select(contract => contract.DocumentFileId!.Value);
        var fromSalaries = dbContext.SalaryRecords.AsNoTracking()
            .Where(record => record.DocumentFileId != null && ids.Contains(record.DocumentFileId.Value))
            .Join(contracts, record => record.ContractId, contract => contract.Id, (record, _) => record.DocumentFileId!.Value);
        var fromAssignments = dbContext.AssignmentRecords.AsNoTracking()
            .Where(record => record.DocumentFileId != null && ids.Contains(record.DocumentFileId.Value))
            .Join(contracts, record => record.ContractId, contract => contract.Id, (record, _) => record.DocumentFileId!.Value);
        var fromRenewals = dbContext.ContractRenewals.AsNoTracking()
            .Where(renewal => renewal.DocumentFileId != null && ids.Contains(renewal.DocumentFileId.Value))
            .Join(contracts, renewal => renewal.ContractId, contract => contract.Id, (renewal, _) => renewal.DocumentFileId!.Value);
        var fromExams = dbContext.MedicalExams.AsNoTracking()
            .Where(exam => exam.TenantId == tenantId && exam.DocumentFileId != null && ids.Contains(exam.DocumentFileId.Value))
            .Select(exam => exam.DocumentFileId!.Value);

        var used = await fromContracts
            .Union(fromSalaries)
            .Union(fromAssignments)
            .Union(fromRenewals)
            .Union(fromExams)
            .ToListAsync(cancellationToken);
        return used.ToHashSet();
    }
}
