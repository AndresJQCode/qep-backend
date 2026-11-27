namespace Modules.Storage.Application;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Referencias»): qué PDF de empleados (<c>FileOwnerType.HrDocument</c>) siguen en uso. Un
/// archivo está en uso mientras lo nombre un contrato, un registro de salario, un registro de cargo y lugar, una prórroga o un examen médico
/// del tenant. Storage no conoce a Hr: lo implementa un adaptador en Bootstrapper. Mismo contrato que <see cref="IChatbotMediaReferences"/>:
/// la respuesta es el subconjunto de <paramref name="fileIds"/> que <paramref name="tenantId"/> usa en Hr; un id que use otro tenant no
/// cuenta. Es una dependencia obligatoria de la purga (no una lista de sondas): sin el adaptador el contenedor no se arma y nada se borra.
/// </summary>
public interface IHrDocumentReferences
{
    Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
