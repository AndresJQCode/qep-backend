namespace Modules.Scheduling.Application;

/// <summary>
/// Todo caso de uso de la Agenda que lee o crea configuración pasa primero por acá: Tenancy no avisa cuando se activa un módulo, así que
/// la sede principal y las reglas por defecto se crean en el primer acceso (spec 2026-11-21, «Aprovisionamiento»). Con las reglas ya
/// creadas el aprovisionador devuelve sin abrir transacción; lo que cuesta es una lectura por clave.
/// </summary>
public sealed class SchedulingFirstAccess(ISchedulingProvisioner provisioner, ISchedulingTenantDirectory tenants)
{
    // Un tenant sin nombre no debería existir; si pasa, la sede igual debe nacer.
    private const string FallbackLocationName = "Sede principal";

    public async Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var name = await tenants.GetDisplayNameAsync(tenantId, cancellationToken);
        await provisioner.EnsureAsync(
            tenantId,
            string.IsNullOrWhiteSpace(name) ? FallbackLocationName : TrimToLocationName(name),
            cancellationToken);
    }

    // `Location.Name` admite 120 caracteres; el nombre del tenant puede ser más largo.
    private static string TrimToLocationName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120].TrimEnd();
    }
}
