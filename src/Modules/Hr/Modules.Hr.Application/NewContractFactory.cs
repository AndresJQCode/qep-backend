using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Arma el <see cref="NewContract"/> del dominio desde el cuerpo del alta, la reincorporación o la conversión de tipo: empresa, PDF,
/// cargo, área, centro y los pisos legales a la fecha de inicio. Todo lo que se puede rechazar con un error de campo se rechaza acá,
/// antes de tocar el agregado. <c>errorPrefix</c> es <c>contract.</c> en el alta y vacío en la reincorporación y la conversión: las
/// claves de <c>errors</c> siguen la forma del cuerpo que llegó.
/// </summary>
public sealed class NewContractFactory(
    HrCompanyRefs companies,
    IHrDocumentStore documents,
    IHrSettingsRepository settings,
    SalaryRules salaryRules)
{
    /// <param name="requireActiveCompany">
    /// El alta, la reincorporación y la importación exigen una empresa activa (un vínculo nuevo, D-12). La conversión de tipo no: es
    /// el mismo empleador, y quien llama ya comparó la empresa con la del vigente.
    /// </param>
    public async Task<(NewContract Contract, SalaryFloor FloorAtStart)> BuildAsync(
        Guid tenantId,
        NewContractRequest request,
        string errorPrefix,
        bool requireActiveCompany,
        CancellationToken cancellationToken)
    {
        var terms = request.Contract!;
        var salary = request.Salary!;
        var assignment = request.Assignment!;
        var companyId = terms.CompanyId!.Value;

        if (requireActiveCompany)
        {
            await companies.RequireActiveAsync(tenantId, companyId, $"{errorPrefix}contract.companyId", cancellationToken);
        }

        var document = await HrDocumentAttachment.ResolveAsync(documents, tenantId, terms.DocumentFileId, cancellationToken);

        var (position, area, workplace, site) = await AssignmentLookups.ResolveAsync(
            settings,
            tenantId,
            assignment.PositionId!.Value,
            assignment.AreaId,
            assignment.WorkplaceId!.Value,
            new AssignmentKeys($"{errorPrefix}assignment.positionId", $"{errorPrefix}assignment.areaId", $"{errorPrefix}assignment.workplaceId"),
            cancellationToken);
        ContractRules.EnsureWorkplaceOfCompany(site, companyId, $"{errorPrefix}assignment.workplaceId");

        var startDate = terms.StartDate!.Value;
        var floor = await salaryRules.FloorAtAsync(startDate, cancellationToken);

        var contract = new NewContract(
            EmploymentContractId.New(),
            new ContractTerms(
                companyId,
                Enum.Parse<ContractType>(terms.ContractType!),
                Enum.Parse<ContractDuration>(terms.Duration!),
                Enum.Parse<SalaryType>(terms.SalaryType!),
                startDate,
                terms.EndDate),
            document,
            SalaryRecordId.New(),
            salary.Amount!.Value,
            salary.Teleworker ?? false,
            salary.AcknowledgeBelowMinimum ?? false,
            AssignmentRecordId.New(),
            position.Id,
            area?.Id,
            site.Id,
            workplace.Id);
        return (contract, floor);
    }
}

/// <summary>Las claves de <c>errors</c> de cada uno: dependen de la forma del cuerpo (anidado en el alta, plano en un registro nuevo).</summary>
internal sealed record AssignmentKeys(string Position, string Area, string Workplace);

/// <summary>
/// Cargo, área y centro de un registro de cargo y lugar: existen, no están archivados y la sede del centro tampoco. Lo comparten el
/// contrato nuevo y el registro nuevo de cargo y lugar, con las claves de error que cada uno pide.
/// </summary>
internal static class AssignmentLookups
{
    public static async Task<(Position Position, Area? Area, Workplace Workplace, Site Site)> ResolveAsync(
        IHrSettingsRepository settings,
        Guid tenantId,
        Guid positionId,
        Guid? areaId,
        Guid workplaceId,
        AssignmentKeys keys,
        CancellationToken cancellationToken)
    {
        var position = await settings.FindPositionAsync(tenantId, new PositionId(positionId), cancellationToken);
        if (position is not { Archived: false })
        {
            HrFieldErrors.Throw(keys.Position, "Elige un cargo vigente.");
        }

        Area? area = null;
        if (areaId is { } id)
        {
            area = await settings.FindAreaAsync(tenantId, new AreaId(id), cancellationToken);
            if (area is not { Archived: false })
            {
                HrFieldErrors.Throw(keys.Area, "Elige un área vigente.");
            }
        }

        var workplace = await settings.FindWorkplaceAsync(tenantId, new WorkplaceId(workplaceId), cancellationToken);
        var site = workplace is { Archived: false }
            ? await settings.FindSiteAsync(tenantId, workplace.SiteId, cancellationToken)
            : null;
        if (workplace is not { Archived: false } || site is not { Archived: false })
        {
            HrFieldErrors.Throw(keys.Workplace, "Elige un centro de trabajo vigente.");
        }

        return (position, area, workplace, site);
    }
}
