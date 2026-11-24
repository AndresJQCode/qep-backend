using System.Text.Json;
using Bootstrapper.Valkey.Permissions;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>
/// <see cref="ILegalParameterSource"/> con <c>Valkey:Areas:LegalParameters</c> prendido (spec 2026-11-22, «Caché»). Lee la versión
/// <c>qep:legalparams:ver</c> <b>antes</b> de cargar y busca la tabla en <c>qep:legalparams:{ver}</c>; si no está, la carga de la base
/// y la guarda bajo <b>esa</b> versión con <c>EX 3600</c>. Quien escribe sube la versión después del commit, así que un lector que
/// arrancó antes del commit deja su tabla vieja bajo la versión vieja, que nadie vuelve a pedir. Cualquier intento que falle (Valkey
/// caído, tope, valor ilegible) lee la base: el gateway ya contó el respaldo, y nunca se tumba el alta de empleados ni la nómina por
/// la caché. Una instancia vive un scope: la tabla se resuelve una vez y atiende las dos listas.
/// </summary>
internal sealed class ValkeyLegalParameterSource(ILegalParameterSource inner, ValkeyGateway gateway, TimeProvider time)
    : ILegalParameterSource
{
    /// <summary>El techo de un bump que no pudo subir la versión: el daño máximo queda en una hora.</summary>
    internal static readonly TimeSpan TableTtl = TimeSpan.FromHours(1);

    private LegalParameterSnapshot? _snapshot;

    public async Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Values;

    public async Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Brackets;

    private async Task<LegalParameterSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } memo)
        {
            return memo;
        }

        // La versión se lee antes de cargar: un bump en el medio deja la tabla nacida vieja.
        if (await ReadVersionAsync(cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return _snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        var tableKey = ValkeyKeys.LegalParametersTable(version);
        var read = await gateway.TryAsync(
            ValkeyArea.LegalParameters,
            database => database.StringGetAsync(tableKey),
            cancellationToken).ConfigureAwait(false);
        if (read is not { Succeeded: true })
        {
            return _snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!read.Value.IsNull && TryRead(read.Value) is { } cached)
        {
            return _snapshot = cached;
        }

        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var json = loaded.Serialize();
        await gateway.TryAsync(
            ValkeyArea.LegalParameters,
            database => database.StringSetAsync(tableKey, json, TableTtl),
            cancellationToken).ConfigureAwait(false);
        return _snapshot = loaded;
    }

    // La versión ausente no vale 0: con allkeys-lru Valkey puede desalojar el contador y una tabla sobreviviente de una versión
    // anterior volvería a coincidir (ABA). Se siembra con la base de tiempo, igual que la versión de acceso del tenant.
    private async Task<long?> ReadVersionAsync(CancellationToken cancellationToken)
    {
        var key = ValkeyKeys.LegalParametersVersion;
        var read = await gateway.TryAsync(
            ValkeyArea.LegalParameters,
            database => database.StringGetAsync(key),
            cancellationToken).ConfigureAwait(false);
        if (read is not { Succeeded: true })
        {
            return null;
        }

        // Ojo: RedisValue.TryParse(out long) acepta un valor nulo y devuelve 0, así que lo ausente se separa antes.
        if (!read.Value.IsNull)
        {
            // Un valor que no es un entero (nadie lo escribe así) no es una versión.
            return read.Value.TryParse(out long stored) ? stored : null;
        }

        var seed = TenantAccessVersionBase.Seed(time);
        var seeded = await gateway.TryAsync(
            ValkeyArea.LegalParameters,
            database => database.StringSetAsync(key, seed, TenantAccessVersionBase.Ttl, When.NotExists),
            cancellationToken).ConfigureAwait(false);
        if (seeded is not { Succeeded: true })
        {
            return null;
        }

        // Otro pod pudo ganar la siembra: se lee el valor que quedó.
        var again = await gateway.TryAsync(
            ValkeyArea.LegalParameters,
            database => database.StringGetAsync(key),
            cancellationToken).ConfigureAwait(false);
        return again is { Succeeded: true, Value.IsNull: false } && again.Value.TryParse(out long current) ? current : null;
    }

    private async Task<LegalParameterSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var values = await inner.ListAsync(cancellationToken).ConfigureAwait(false);
        var brackets = await inner.ListBracketsAsync(cancellationToken).ConfigureAwait(false);
        return new LegalParameterSnapshot(values, brackets);
    }

    private static LegalParameterSnapshot? TryRead(RedisValue value)
    {
        try
        {
            return LegalParameterSnapshot.Deserialize((string)value!);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // Una tabla ilegible (otra versión del despliegue, truncada) se trata como ausente y se reescribe.
            return null;
        }
    }
}
