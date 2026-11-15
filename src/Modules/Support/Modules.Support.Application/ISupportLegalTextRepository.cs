using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Las versiones de los textos legales (plan F16). Prueba legal, insert-only: no hay método que edite ni borre. Dos
/// publicaciones de la misma versión chocan con <c>IX_legal_text_versions_tenant_version</c>: <c>support.legal_texts.version_taken</c>.</summary>
public interface ISupportLegalTextRepository
{
    /// <summary>La de mayor versión del tenant, o null si nunca publicó (se muestra <see cref="SupportLegalTextVersion.Example"/>).</summary>
    Task<SupportLegalTextVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SupportLegalTextVersion?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    void Add(SupportLegalTextVersion text);
}
