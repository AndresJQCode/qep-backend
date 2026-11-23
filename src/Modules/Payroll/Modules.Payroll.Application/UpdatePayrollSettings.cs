using BuildingBlocks.Application;
using FluentValidation;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; crudo: el caso de uso lo lee ya autorizado. <c>"0"</c> en la primera escritura.</param>
public sealed record UpdatePayrollSettingsCommand(Guid TenantId, Guid CompanyId, PayrollSettingsRequest Request, string? IfMatch)
    : ICommand<PayrollSettingsDto>;

/// <summary>
/// Guarda la configuración de la empresa. Exige sólo <c>payroll.settings.manage</c> (decisión del owner, 2026-11-21: sin
/// <c>PeriodRead</c>). Mientras nunca se guardó el <c>GET</c> responde <c>version: 0</c> y el primer guardado va con
/// <c>If-Match: "0"</c> (R-17); otro guardado con <c>"0"</c> después es 412. Dos primeros guardados a la vez los separa el índice
/// único, que la unidad de trabajo traduce a 412. Una empresa inactiva es 422
/// <c>payroll.company.inactive</c>; el <c>GET</c> la sigue respondiendo.
/// </summary>
public sealed class UpdatePayrollSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    ICompanyPayrollSettingsRepository settings,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    PayrollSettingsComposer composer,
    IValidator<PayrollSettingsRequest> validator)
    : ICommandHandler<UpdatePayrollSettingsCommand, PayrollSettingsDto>
{
    public async Task<PayrollSettingsDto> HandleAsync(UpdatePayrollSettingsCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, PayrollPermissions.SettingsManage);
        var expectedVersion = PayrollIfMatch.RequireAllowingZero(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var company = await companies.RequireAsync(command.TenantId, command.CompanyId, cancellationToken);
        PayrollCompanies.EnsureActive(company, "An inactive company cannot change its payroll settings.");
        var request = command.Request;
        var frequency = Enum.Parse<TransportFrequency>(request.TransportFrequency!);
        var capBase = Enum.Parse<TransportCapBase>(request.TransportCapBase!);
        var overtimeBase = Enum.Parse<OvertimeBase>(request.OvertimeBase!);
        var exonerated = request.Exonerated!.Value;
        var dualControl = request.DualControl!.Value;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        var saved = await settings.FindAsync(command.TenantId, command.CompanyId, cancellationToken);
        var changed = new List<string>(5);
        if (saved is null)
        {
            PayrollSupport.EnsureVersion(0, expectedVersion);
            saved = CompanyPayrollSettings.Create(
                CompanyPayrollSettingsId.New(), command.TenantId, command.CompanyId,
                frequency, capBase, overtimeBase, exonerated, dualControl, now);
            settings.Add(saved);
            changed.AddRange(["transportFrequency", "transportCapBase", "overtimeBase", "exonerated", "dualControl"]);
        }
        else
        {
            PayrollSupport.EnsureVersion(saved.Version, expectedVersion);
            PayrollSupport.Diff(changed, "transportFrequency", saved.TransportFrequency, frequency);
            PayrollSupport.Diff(changed, "transportCapBase", saved.TransportCapBase, capBase);
            PayrollSupport.Diff(changed, "overtimeBase", saved.OvertimeBase, overtimeBase);
            PayrollSupport.Diff(changed, "exonerated", saved.Exonerated, exonerated);
            PayrollSupport.Diff(changed, "dualControl", saved.DualControl, dualControl);
            saved.Update(frequency, capBase, overtimeBase, exonerated, dualControl, now);
        }

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.SettingsUpdated,
            PayrollAuditActions.Resources.Settings, command.CompanyId.ToString(), changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(command.TenantId, company, saved, cancellationToken);
    }
}
