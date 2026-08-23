using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class ClientClassificationRepository(CustomersDbContext dbContext)
    : IClientClassificationRepository
{
    private const string LikeEscapeCharacter = "\\";

    // Mismo escape que CustomerRepository: la barra primero, para no convertir en literal la barra
    // que agregan los otros dos reemplazos.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);


    public async Task<IReadOnlyList<ClientClassification>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.ClientClassifications
            .AsNoTracking()
            .Where(classification => classification.TenantId == tenantId)
            .OrderBy(classification => classification.Name)
            .ToListAsync(cancellationToken);

    // Con tracking a proposito, a diferencia de ListAsync: los llamadores de este mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<ClientClassification?> FindAsync(
        Guid tenantId,
        ClientClassificationId classificationId,
        CancellationToken cancellationToken) =>
        dbContext.ClientClassifications.SingleOrDefaultAsync(
            classification =>
                classification.TenantId == tenantId && classification.Id == classificationId,
            cancellationToken);

    public async Task<IReadOnlyList<ClientClassification>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<ClientClassificationId> classificationIds,
        CancellationToken cancellationToken) =>
        await dbContext.ClientClassifications
            .AsNoTracking()
            .Where(classification =>
                classification.TenantId == tenantId &&
                classificationIds.Contains(classification.Id))
            .ToListAsync(cancellationToken);

    // ILike, no ToLower(): comparar con ToLower() dispara los analizadores de sensibilidad
    // cultural (CA1304/CA1311/CA1862) y, peor, ToLower() sin cultura invariante puede comparar
    // distinto segun el locale del servidor. ILike es la comparacion case-insensitive nativa de
    // Npgsql; sin comodines en el patron (los que trae el nombre se escapan) es una igualdad
    // exacta, insensible a mayusculas — lo que el Excel necesita, porque lo llena una persona y
    // "mayorista" y "Mayorista" son la misma clasificacion para quien lo tipea.
    public Task<ClientClassification?> FindByNameAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var pattern = EscapeLikeWildcards(name.Trim());
        return dbContext.ClientClassifications
            .AsNoTracking()
            .SingleOrDefaultAsync(
                classification =>
                    classification.TenantId == tenantId &&
                    EF.Functions.ILike(classification.Name, pattern, LikeEscapeCharacter),
                cancellationToken);
    }

    public void Add(ClientClassification classification) =>
        dbContext.ClientClassifications.Add(classification);

    public void Remove(ClientClassification classification) =>
        dbContext.ClientClassifications.Remove(classification);
}
