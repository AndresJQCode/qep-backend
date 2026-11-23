using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// La escritura de los valores legales desde la consola del operador. Distinta de <see cref="ILegalParameterSource"/>, que es la
/// lectura sin seguimiento que usa el resolver: acá las listas vienen con seguimiento (para editar) y sin caché. Dato de la
/// plataforma, sin <c>tenantId</c>. Nada se guarda hasta <see cref="IHrUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public interface ILegalParameterRepository
{
    Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken);

    void Add(LegalParameterValue value);

    void Remove(LegalParameterValue value);

    void AddBrackets(IEnumerable<LegalParameterBracket> brackets);

    void RemoveBrackets(IEnumerable<LegalParameterBracket> brackets);

    void AddChange(LegalParameterChange change);

    /// <summary>El historial, lo más reciente primero (<c>changed_at</c> y luego <c>id</c>, descendentes), con el total.</summary>
    Task<(IReadOnlyList<LegalParameterChange> Items, int Total)> ListChangesAsync(
        int page, int pageSize, CancellationToken cancellationToken);
}
