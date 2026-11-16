using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Las versiones de los textos legales de la fase 3 (plan F16), <b>sólo lectura</b>: no tienen escritor desde spec 2026-11-15 §5.4
/// (plan L10). Interino: lo leen todavía <c>GetPublicForm</c> y <c>FilePublicTicket</c> hasta que el Bloque B del plan 2026-11-15 los
/// pase a <see cref="ISupportLegalDocumentRepository"/>; ahí se borra.
/// </summary>
public interface ISupportLegalTextRepository
{
    /// <summary>La de mayor versión del tenant, o null si nunca publicó.</summary>
    Task<SupportLegalTextVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken);
}
