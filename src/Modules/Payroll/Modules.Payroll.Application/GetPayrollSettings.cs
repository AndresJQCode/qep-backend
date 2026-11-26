using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Arma el <see cref="PayrollSettingsDto"/> de una empresa: lo guardado, o los valores por defecto con versión 0 (R-17).</summary>
public sealed class PayrollSettingsComposer(ICompanyPayrollSettingsRepository settings, ConceptCatalog catalog)
{
    public async Task<PayrollSettingsDto> ComposeAsync(
        Guid tenantId, PayrollCompanyRef company, CompanyPayrollSettings? saved, CancellationToken cancellationToken)
    {
        saved ??= await settings.FindAsync(tenantId, company.Id, cancellationToken);
        var concepts = (await catalog.LoadAsync(tenantId, company.Id, cancellationToken)).ToDtos();
        var companyDto = PayrollCompanies.ToDto(company);

        if (saved is null)
        {
            var defaults = CompanyPayrollSettings.DefaultsFor();
            return new PayrollSettingsDto(
                companyDto,
                defaults.TransportFrequency.ToString(),
                defaults.TransportCapBase.ToString(),
                defaults.OvertimeBase.ToString(),
                defaults.Exonerated,
                CompanyPayrollSettings.DefaultDualControl,
                0,
                concepts);
        }

        return new PayrollSettingsDto(
            companyDto,
            saved.TransportFrequency.ToString(),
            saved.TransportCapBase.ToString(),
            saved.OvertimeBase.ToString(),
            saved.Exonerated,
            saved.DualControl,
            saved.Version,
            concepts);
    }
}

public sealed record GetPayrollSettingsQuery(Guid TenantId, Guid CompanyId) : IQuery<PayrollSettingsDto>;

/// <summary>Basta uno de los dos permisos de lectura: un rol con sólo configurar usa la pantalla (<see cref="PayrollPolicies.SettingsView"/>).</summary>
public sealed class GetPayrollSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    PayrollCompanies companies,
    PayrollSettingsComposer composer)
    : IQueryHandler<GetPayrollSettingsQuery, PayrollSettingsDto>
{
    public async Task<PayrollSettingsDto> HandleAsync(GetPayrollSettingsQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAnyAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.SettingsManage);

        var company = await companies.RequireAsync(query.TenantId, query.CompanyId, cancellationToken);
        return await composer.ComposeAsync(query.TenantId, company, null, cancellationToken);
    }
}
