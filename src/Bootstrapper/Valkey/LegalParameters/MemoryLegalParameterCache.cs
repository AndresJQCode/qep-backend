using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>
/// La copia de la tabla de valores legales del pod, la que rige con <c>Valkey:Areas:LegalParameters</c> apagado (spec 2026-11-22,
/// «Caché»): sin Valkey no hay invalidación entre réplicas, así que la entrada vive a lo sumo <see cref="EntryTtl"/> (60 s), y el
/// pod que escribe la invalida al instante con el contador local. Es el mismo diseño que <c>MemoryPermissionCache</c>: la entrada
/// guarda la versión con la que se cargó y sirve sólo mientras coincida con la actual; el lector toma la versión <b>antes</b> de
/// cargar, así que un bump en el medio deja la entrada nacida vieja.
/// </summary>
internal sealed class MemoryLegalParameterCache(TimeProvider time)
{
    public static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private long _version;
    private Entry? _entry;

    public long CurrentVersion => Interlocked.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);

    public bool TryGet(out LegalParameterSnapshot? snapshot)
    {
        snapshot = null;
        lock (_gate)
        {
            if (_entry is not { } entry || entry.Version != CurrentVersion || time.GetUtcNow() >= entry.ExpiresAt)
            {
                return false;
            }

            snapshot = entry.Snapshot;
            return true;
        }
    }

    /// <param name="version">La versión leída <b>antes</b> de cargar: un bump en el medio deja la entrada nacida vieja.</param>
    public void Set(long version, LegalParameterSnapshot snapshot)
    {
        lock (_gate)
        {
            // Una carga lenta que terminó tarde no pisa a una más nueva.
            if (_entry is { } current && current.Version > version)
            {
                return;
            }

            _entry = new Entry(version, snapshot, time.GetUtcNow() + EntryTtl);
        }
    }

    private sealed record Entry(long Version, LegalParameterSnapshot Snapshot, DateTimeOffset ExpiresAt);
}

/// <summary><see cref="ILegalParameterSource"/> con el área apagada: la copia del pod delante de la base.</summary>
internal sealed class MemoryLegalParameterSource(ILegalParameterSource inner, MemoryLegalParameterCache cache)
    : ILegalParameterSource
{
    public async Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Values;

    public async Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Brackets;

    private async Task<LegalParameterSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGet(out var cached))
        {
            return cached!;
        }

        var version = cache.CurrentVersion;
        var values = await inner.ListAsync(cancellationToken).ConfigureAwait(false);
        var brackets = await inner.ListBracketsAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = new LegalParameterSnapshot(values, brackets);
        cache.Set(version, snapshot);
        return snapshot;
    }
}
