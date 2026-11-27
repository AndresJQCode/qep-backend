namespace Modules.Hr.Application;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Referencias»): qué PDF nombra todavía Hr. La respuesta es el subconjunto de
/// <paramref name="fileIds"/> que un <c>document_file_id</c> de <paramref name="tenantId"/> nombra en contratos, registros de salario,
/// registros de cargo y lugar, prórrogas o exámenes médicos; un id que use otro tenant no cuenta. Lo usa el adaptador de Bootstrapper que le
/// responde a Storage antes de purgar: una tabla que faltara acá dejaría purgar un documento laboral vivo, sin forma de recuperarlo.
/// </summary>
public interface IHrDocumentUsage
{
    Task<IReadOnlySet<Guid>> FindUsedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}
