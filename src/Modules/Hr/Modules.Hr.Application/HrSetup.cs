namespace Modules.Hr.Application;

/// <summary>
/// ¿Está lista la configuración para dar de alta empleados? Hay una empresa activa con una sede y un centro de trabajo sin
/// archivar, y un cargo sin archivar. Toda lectura de <c>setupComplete</c> y todo <c>SetupIncomplete</c> pasa por acá.
/// </summary>
public sealed class HrSetup(IHrCompanyLookup companies, IHrSettingsRepository settings)
{
    public async Task<bool> IsCompleteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var active = await companies.ListActiveAsync(tenantId, cancellationToken);
        return await settings.IsSetupCompleteAsync(tenantId, active.Select(company => company.Id).ToList(), cancellationToken);
    }
}
