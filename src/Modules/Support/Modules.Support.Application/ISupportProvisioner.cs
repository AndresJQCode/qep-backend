namespace Modules.Support.Application;

/// <summary>Plan D1: Tenancy no avisa cuando se prende un módulo, así que la fila de soporte del tenant y sus ocho tipos se
/// siembran en el primer acceso. Idempotente y segura ante dos primeros accesos a la vez.</summary>
public interface ISupportProvisioner
{
    Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken);
}
