using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportSettingsRepository
{
    /// <summary>La fila del tenant, con tracking. Se llama después de <see cref="ISupportProvisioner.EnsureAsync"/>: sin la fila,
    /// lanza.</summary>
    Task<SupportTenantSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
