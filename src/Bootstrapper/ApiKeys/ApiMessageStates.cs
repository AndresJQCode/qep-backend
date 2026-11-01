using Modules.ApiAccess.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// ApiAccess no ve Messaging: el estado de cada mensaje entra por este adaptador. Usa
/// <c>ExternalMessageState.Describe</c>, la misma traducción que lee el integrador, para que la pantalla
/// y la API externa nunca digan cosas distintas del mismo mensaje.
/// </summary>
internal sealed class ApiMessageStates(IExternalMessageQueries messages) : IApiMessageStates
{
    public async Task<IReadOnlyDictionary<Guid, ApiMessageState>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken) =>
        (await messages.FindManyAsync(tenantId, messageIds, cancellationToken))
            .ToDictionary(
                row => row.Id,
                row =>
                {
                    var state = ExternalMessageState.Describe(row.Status, row.FailureCode);
                    return new ApiMessageState(state.Status, state.FailureCode, state.FailureReason);
                });

    public Task<IReadOnlyList<Guid>> ListFailedIdsAsync(
        Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
        messages.ListFailedIdsAsync(tenantId, since, cancellationToken);
}
