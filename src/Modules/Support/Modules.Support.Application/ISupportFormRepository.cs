using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Las versiones del formulario público (plan F14). Insert-only: no hay método que edite ni borre. Dos guardados de la
/// misma versión chocan con <c>IX_form_versions_tenant_version</c>: <c>support.form.version_taken</c>.</summary>
public interface ISupportFormRepository
{
    /// <summary>La de mayor versión del tenant, o null si nunca guardó una (el formulario es <see cref="SupportFormVersion.Default"/>).</summary>
    Task<SupportFormVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SupportFormVersion?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    void Add(SupportFormVersion form);
}
