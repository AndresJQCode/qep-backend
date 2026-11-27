using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Tarjeta 0pQhlDt5: editar una conexión de Siigo con las credenciales de <b>otra cuenta</b> deja la configuración del asistente con ids de
/// la anterior. Integrations no referencia Siigo y Siigo no referencia Integrations (<c>IntegrationsLayerTests</c>, <c>SiigoLayerTests</c>),
/// así que el puerto <see cref="IConnectionCredentialChangeGuard"/> de Integrations se cablea acá con <see cref="ISiigoAccountChange"/>.
/// Los demás proveedores no pasan por aquí: devuelven <c>false</c> y no tocan nada.
///
/// **El borrado corre sobre la conexión y la transacción del <see cref="IntegrationsDbContext"/> del scope.** El
/// <see cref="SiigoDbContext"/> del mismo scope se enrola en ellas antes de borrar, como en <c>CatalogProductDeletionCleanup</c>: la
/// configuración borrada, su auditoría y la conexión editada confirman juntas con el guardado de Integrations, o ninguna queda. El
/// enrolamiento dura lo que el scope: después del commit, ese <see cref="SiigoDbContext"/> sigue apuntando a una transacción terminada,
/// así que **nada en el mismo scope debe volver a usarlo**. La comparación (<see cref="MustResetConfigurationAsync"/>) sólo lee y corre
/// antes, fuera de la transacción. Nunca imprime ni guarda las credenciales.
/// </summary>
internal sealed class SiigoCredentialChangeGuard(
    ISiigoAccountChange accountChange,
    IntegrationsDbContext integrations,
    SiigoDbContext siigo) : IConnectionCredentialChangeGuard
{
    private static bool IsSiigo(string providerKey) =>
        string.Equals(providerKey, IntegrationProviders.Siigo.Key, StringComparison.Ordinal);

    public Task<bool> MustResetConfigurationAsync(
        string providerKey,
        Guid tenantId,
        Guid connectionId,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(secrets);
        if (!IsSiigo(providerKey)
            || fields.GetValueOrDefault(SiigoFieldKeys.Username) is not { Length: > 0 } username
            || secrets.GetValueOrDefault(SiigoFieldKeys.AccessKey) is not { Length: > 0 } accessKey)
        {
            // Sin usuario y clave no hay con qué leer las listas (la prueba de credenciales ya habría fallado).
            return Task.FromResult(false);
        }

        return accountChange.MustResetAsync(
            tenantId, connectionId, new SiigoCredentials(connectionId, username, accessKey), confirmed, cancellationToken);
    }

    public async Task ResetConfigurationAsync(string providerKey, Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!IsSiigo(providerKey))
        {
            return;
        }

        var transaction = integrations.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Resetting the Siigo configuration must run inside the Integrations transaction.");

        siigo.Database.SetDbConnection(integrations.Database.GetDbConnection(), contextOwnsConnection: false);
        await siigo.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        await accountChange.ResetAsync(tenantId, connectionId, cancellationToken);
    }
}
